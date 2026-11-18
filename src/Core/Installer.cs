using System.Diagnostics;
using System.IO.Compression;

namespace TetzModManager;

/// <summary>One mod found in an archive, and where it will go.</summary>
public sealed class PlannedMod
{
	public required ModManifest Manifest { get; init; }
	/// <summary>The mod's folder inside the extracted archive.</summary>
	public required string Source { get; init; }
	/// <summary>The folder in Mods it goes to: the installed copy's folder when the mod is already installed.</summary>
	public required string Target { get; init; }
	public InstalledMod? Existing { get; init; }
	/// <summary>Why it can't be installed (another mod already uses the target folder), or null.</summary>
	public string? Blocked { get; init; }
	public bool ReplacesVortexFolder => Existing?.VortexManaged == true || File.Exists(Path.Combine(Target, Scanner.VortexMarker));
}

public sealed class InstallPlan : IDisposable
{
	public required string ArchivePath { get; init; }
	public required string ExtractDir { get; init; }
	public List<PlannedMod> Mods { get; } = [];

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(ExtractDir))
			{
				Directory.Delete(ExtractDir, true);
			}
		}
		catch (IOException)
		{
		}
	}
}

/// <summary>Where an install came from, kept with the mod's record.</summary>
public sealed record NexusSource(int ModId, int FileId, string FileName, string? FileVersion);

public static class Installer
{
	private static readonly string SevenZip = @"C:\Program Files\7-Zip\7z.exe";

	/// <summary>Extracts the archive and works out each mod in it (a folder holding Info.json; the archive root counts).</summary>
	public static InstallPlan Plan(Settings settings, string archive, IReadOnlyList<InstalledMod> installed)
	{
		string extract = Path.Combine(Settings.WorkDir, Guid.NewGuid().ToString("N")[..12]);
		Directory.CreateDirectory(extract);
		InstallPlan plan = new() { ArchivePath = archive, ExtractDir = extract };
		try
		{
			Extract(archive, extract);
			string baseName = CleanName(Path.GetFileNameWithoutExtension(archive));
			foreach (string root in FindRoots(extract))
			{
				ModManifest? m = ModManifest.ReadFolder(root);
				if (m == null)
				{
					continue;
				}
				InstalledMod? existing = installed.FirstOrDefault(i => string.Equals(i.Key, m.Key, StringComparison.OrdinalIgnoreCase));
				string folderName = root == extract ? SafeFolderName(m.Name, baseName) : Path.GetFileName(root);
				string target = existing?.Folder ?? Path.Combine(settings.ModsDir, folderName);
				string? blocked = null;
				if (existing == null)
				{
					InstalledMod? occupant = installed.FirstOrDefault(i => string.Equals(i.Folder, target, StringComparison.OrdinalIgnoreCase));
					if (occupant != null)
					{
						blocked = $"The folder \"{folderName}\" already holds {occupant.Manifest.Name} ({occupant.Key}).";
					}
				}
				plan.Mods.Add(new PlannedMod { Manifest = m, Source = root, Target = target, Existing = existing, Blocked = blocked });
			}
		}
		catch
		{
			plan.Dispose();
			throw;
		}
		if (plan.Mods.Count == 0)
		{
			plan.Dispose();
			throw new InvalidDataException($"No mod found in {Path.GetFileName(archive)} (no folder with an Info.json).");
		}
		return plan;
	}

	/// <summary>
	/// Installs the planned mods: the current folder is moved to the rollback folder, the new files are copied in (plain
	/// copies, no links), Preferences.json gets the version (or a new enabled entry), and the archive is filed.
	/// </summary>
	public static List<string> Apply(Settings settings, StateStore state, InstallPlan plan, NexusSource? nexus, Action<string> log)
	{
		Game.EnsureClosed(settings);
		Preferences prefs = Preferences.Load(settings.PreferencesPath);
		List<string> keys = [];
		foreach (PlannedMod p in plan.Mods)
		{
			if (p.Blocked != null)
			{
				log($"Skipped {p.Manifest.Name}: {p.Blocked}");
				continue;
			}
			string? previous = null;
			if (Directory.Exists(p.Target))
			{
				previous = KeepPrevious(p.Target, p.Manifest.Key);
			}
			CopyDirectory(p.Source, p.Target);
			string what = p.Existing == null ? $"Installed {p.Manifest.Name} {p.Manifest.Version}" : $"Replaced {p.Manifest.Name} {p.Existing.Manifest.Version} with {p.Manifest.Version}";
			log($"{what} in \"{Path.GetFileName(p.Target)}\"" + (previous != null ? " (previous version kept for rollback)" : ""));
			if (p.Manifest.Identifier is string id)
			{
				bool added = prefs.SetVersionOrAdd(id, p.Manifest.Version, p.Manifest.Scope);
				if (added)
				{
					log($"  added {id} to Preferences.json, enabled");
				}
			}
			ModRecord r = state.Get(p.Manifest.Key);
			r.InstalledVersion = p.Manifest.Version;
			r.InstalledAt = DateTime.Now;
			if (nexus == null)
			{
				r.InstalledNexusVersion = null;
				r.NexusFileId = null;
			}
			else
			{
				r.NexusModId = nexus.ModId;
				r.NexusFileId = nexus.FileId;
				r.NexusFileName = nexus.FileName;
				r.InstalledNexusVersion = nexus.FileVersion;
				r.LinkSource = "nxm";
				if (r.LatestFileId == nexus.FileId || (r.LatestVersion != null && Versions.Compare(r.LatestVersion, p.Manifest.Version) <= 0))
				{
					r.LatestVersion = p.Manifest.Version;
					r.LatestFileId = nexus.FileId;
				}
			}
			keys.Add(p.Manifest.Key);
		}
		if (keys.Count > 0)
		{
			prefs.Save();
			string archived = ModArchive.Store(settings, state, plan.ArchivePath, keys, nexus?.FileName);
			log($"  archive: {archived}");
		}
		state.Save();
		return keys;
	}

	/// <summary>Moves a mod's folder out of Mods, and the copy kept from before back in.</summary>
	public static void RollBack(Settings settings, StateStore state, InstalledMod mod, Action<string> log)
	{
		Game.EnsureClosed(settings);
		string kept = PreviousPath(mod.Key);
		if (!Directory.Exists(kept))
		{
			throw new InvalidOperationException($"No earlier version of {mod.Manifest.Name} is kept.");
		}
		ModManifest back = ModManifest.ReadFolder(kept) ?? throw new InvalidDataException("The kept copy has no readable Info.json.");
		string swap = kept + ".swap";
		MoveDirectory(mod.Folder, swap);
		MoveDirectory(kept, mod.Folder);
		MoveDirectory(swap, kept);
		if (back.Identifier is string id)
		{
			Preferences prefs = Preferences.Load(settings.PreferencesPath);
			prefs.SetVersionOrAdd(id, back.Version, back.Scope);
			prefs.Save();
		}
		ModRecord r = state.Get(mod.Key);
		r.InstalledVersion = back.Version;
		r.NexusFileId = null;
		r.InstalledNexusVersion = null;
		state.Save();
		log($"Rolled back {mod.Manifest.Name}: {mod.Manifest.Version} -> {back.Version} (the newer one is kept, so this can be undone the same way)");
	}

	public static bool HasPrevious(string key) => Directory.Exists(PreviousPath(key));

	/// <summary>Moves the folder out of Mods (kept for rollback) and drops the Preferences.json entry.</summary>
	public static void Uninstall(Settings settings, StateStore state, InstalledMod mod, Action<string> log)
	{
		Game.EnsureClosed(settings);
		KeepPrevious(mod.Folder, mod.Key);
		if (mod.Manifest.Identifier is string id)
		{
			Preferences prefs = Preferences.Load(settings.PreferencesPath);
			if (prefs.Remove(id))
			{
				prefs.Save();
			}
		}
		ModRecord? r = state.Find(mod.Key);
		if (r != null)
		{
			r.InstalledVersion = null;
			r.InstalledAt = null;
			state.Save();
		}
		log($"Uninstalled {mod.Manifest.Name} (its folder is kept in {PreviousPath(mod.Key)}, its archive stays)");
	}

	private static string PreviousPath(string key) =>
		Path.Combine(Settings.PreviousDir, string.Concat(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c)));

	private static string KeepPrevious(string folder, string key)
	{
		string dest = PreviousPath(key);
		Directory.CreateDirectory(Settings.PreviousDir);
		if (Directory.Exists(dest))
		{
			DeleteDirectory(dest);
		}
		MoveDirectory(folder, dest);
		return dest;
	}

	public static void Extract(string archive, string dest)
	{
		if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
		{
			ZipFile.ExtractToDirectory(archive, dest, true);
			return;
		}
		if (!File.Exists(SevenZip))
		{
			throw new NotSupportedException($"{Path.GetExtension(archive)} archives need 7-Zip ({SevenZip}).");
		}
		ProcessStartInfo psi = new(SevenZip) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
		psi.ArgumentList.Add("x");
		psi.ArgumentList.Add("-y");
		psi.ArgumentList.Add("-o" + dest);
		psi.ArgumentList.Add(archive);
		using Process p = Process.Start(psi)!;
		p.StandardOutput.ReadToEnd();
		string err = p.StandardError.ReadToEnd();
		p.WaitForExit();
		if (p.ExitCode != 0)
		{
			throw new InvalidDataException($"7-Zip couldn't extract {Path.GetFileName(archive)}: {err.Trim()}");
		}
	}

	/// <summary>Folders holding an Info.json, shallowest first; nothing below a mod folder counts as another mod.</summary>
	public static List<string> FindRoots(string dir)
	{
		List<string> roots = [];
		Queue<string> queue = new([dir]);
		while (queue.Count > 0)
		{
			string d = queue.Dequeue();
			if (ModManifest.FindFile(d) != null)
			{
				roots.Add(d);
				continue;
			}
			foreach (string sub in Directory.EnumerateDirectories(d).Order(StringComparer.OrdinalIgnoreCase))
			{
				queue.Enqueue(sub);
			}
		}
		return roots;
	}

	private static string CleanName(string archiveName) =>
		System.Text.RegularExpressions.Regex.Replace(archiveName, @"[\s-]+v?\d+(\.\d+)*[a-z]?(\s*\(.*\))?$", "").Trim();

	private static string SafeFolderName(string name, string fallback)
	{
		string s = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
		return s.Length > 0 ? s : fallback;
	}

	private static void CopyDirectory(string from, string to)
	{
		Directory.CreateDirectory(to);
		foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
		{
			Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
		}
		foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
		{
			File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), true);
		}
	}

	private static void MoveDirectory(string from, string to)
	{
		if (string.Equals(Path.GetPathRoot(Path.GetFullPath(from)), Path.GetPathRoot(Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase))
		{
			Directory.Move(from, to);
			return;
		}
		CopyDirectory(from, to);
		DeleteDirectory(from);
	}

	private static void DeleteDirectory(string dir)
	{
		foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
		{
			File.SetAttributes(f, FileAttributes.Normal);
		}
		Directory.Delete(dir, true);
	}
}

/// <summary>The zip archive: every installed archive is filed in the archive folder, keeping one per mod.</summary>
public static class ModArchive
{
	public static string Store(Settings settings, StateStore state, string archive, IReadOnlyList<string> keys, string? name)
	{
		Directory.CreateDirectory(settings.ArchiveDir);
		string fileName = name ?? Path.GetFileName(archive);
		string dest = Path.Combine(settings.ArchiveDir, fileName);
		// Another mod's archive with the same file name stays: this one gets a numbered name.
		for (int n = 2; state.Mods.Any(kv => !keys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)
			&& string.Equals(kv.Value.Archive, dest, StringComparison.OrdinalIgnoreCase)); n++)
		{
			dest = Path.Combine(settings.ArchiveDir, $"{Path.GetFileNameWithoutExtension(fileName)} ({n}){Path.GetExtension(fileName)}");
		}
		if (!string.Equals(Path.GetFullPath(archive), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
		{
			File.Copy(archive, dest, true);
		}
		foreach (string key in keys)
		{
			ModRecord r = state.Get(key);
			string? old = r.Archive;
			r.Archive = dest;
			if (old != null && !string.Equals(old, dest, StringComparison.OrdinalIgnoreCase)
				&& !state.Mods.Values.Any(o => string.Equals(o.Archive, old, StringComparison.OrdinalIgnoreCase))
				&& File.Exists(old))
			{
				File.Delete(old);
			}
		}
		return dest;
	}
}

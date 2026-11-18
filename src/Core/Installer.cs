using System.Diagnostics;
using System.IO.Compression;

namespace TetzModManager;

public enum PlanAction
{
	/// <summary>Not installed yet.</summary>
	Install,
	/// <summary>Newer than the installed copy.</summary>
	Update,
	/// <summary>The same version as installed (copied again, except for a shared library).</summary>
	Reinstall,
	/// <summary>Older than the installed copy (asked first).</summary>
	Downgrade,
	/// <summary>A library another installed package carries too, already at this version or newer: left as it is.</summary>
	KeepInstalled,
	/// <summary>Another mod already uses the folder.</summary>
	Blocked,
}

/// <summary>One mod found in an archive, and what installing it does.</summary>
public sealed class PlannedMod
{
	public required ModManifest Manifest { get; init; }
	/// <summary>The mod's folder inside the extracted archive.</summary>
	public required string Source { get; init; }
	/// <summary>The folder in Mods it goes to: the installed copy's folder when the mod is already installed.</summary>
	public required string Target { get; init; }
	public InstalledMod? Existing { get; init; }
	public required PlanAction Action { get; init; }
	/// <summary>Carried by another installed package too (a bundled library such as GP38Scripts).</summary>
	public bool Shared { get; init; }
	public string? Reason { get; init; }
	public bool Copies => Action is PlanAction.Install or PlanAction.Update or PlanAction.Reinstall or PlanAction.Downgrade;
	public bool ReplacesVortexFolder => Copies && (Existing?.VortexManaged == true || File.Exists(Path.Combine(Target, Scanner.VortexMarker)));
}

/// <summary>Add-on files for an installed mod's folder (an archive without an Info.json whose top folder is that mod's folder).</summary>
public sealed class PlannedOverlay
{
	public required InstalledMod Target { get; init; }
	public required string Source { get; init; }
	public required List<string> Files { get; init; }
}

public sealed class InstallPlan : IDisposable
{
	/// <summary>The archive, or the folder dropped on the window.</summary>
	public required string SourcePath { get; init; }
	public bool IsFolder { get; init; }
	public required string ExtractDir { get; init; }
	public bool OwnsExtractDir { get; init; }
	public List<PlannedMod> Mods { get; } = [];
	public List<PlannedOverlay> Overlays { get; } = [];
	/// <summary>The installed package this one updates (the one carrying the same mods), or null for a new package.</summary>
	public PackageRecord? Package { get; set; }
	public string Name { get; set; } = "";
	/// <summary>Several mods, none named like the archive ("Freeman Test"): the package keeps the archive's name.</summary>
	public bool NamedAfterArchive { get; set; }

	public void Dispose()
	{
		if (OwnsExtractDir)
		{
			Files.TryDeleteDirectory(ExtractDir);
		}
	}
}

/// <summary>Where an install came from, kept with the package.</summary>
public sealed record NexusSource(int ModId, int FileId, string FileName, string? FileVersion);

public static class Installer
{
	/// <summary>Extracts the archive (or takes the folder) and works out every mod in it and what installing does.</summary>
	public static InstallPlan Plan(Settings settings, string source, ScanResult installed, StateStore state, int? nexusModId = null)
	{
		bool isFolder = Directory.Exists(source);
		string extract = isFolder ? source : Path.Combine(Settings.WorkDir, Guid.NewGuid().ToString("N")[..12]);
		InstallPlan plan = new() { SourcePath = source, IsFolder = isFolder, ExtractDir = extract, OwnsExtractDir = !isFolder };
		try
		{
			if (!isFolder)
			{
				Directory.CreateDirectory(extract);
				Files.Extract(source, extract);
			}
			List<(string Root, ModManifest Manifest)> found = Files.FindRoots(extract)
				.Select(r => (r, ModManifest.ReadFolder(r)))
				.Where(x => x.Item2 != null)
				.Select(x => (x.r, x.Item2!))
				.ToList();
			plan.Package = state.Match(found.Select(f => f.Manifest).ToList(),
				k => installed.Mods.FirstOrDefault(m => string.Equals(m.Key, k, StringComparison.OrdinalIgnoreCase))?.Manifest, nexusModId);
			foreach ((string root, ModManifest m) in found)
			{
				plan.Mods.Add(PlanMod(settings, plan, root, m, installed, state));
			}
			if (found.Count == 0)
			{
				PlanOverlays(plan, installed);
			}
			string archiveName = CleanName(Path.GetFileNameWithoutExtension(source.TrimEnd('\\', '/')));
			plan.NamedAfterArchive = plan.Mods.Count(p => p.Action != PlanAction.Blocked) > 1
				&& !plan.Mods.Any(p => Scanner.Normalize(p.Manifest.Name) is { Length: >= 3 } mod && Scanner.Normalize(archiveName) is { Length: >= 3 } arc
					&& (mod.Contains(arc) || arc.Contains(mod)));
			plan.Name = plan.Package?.Name
				?? (plan.NamedAfterArchive ? archiveName : null)
				?? plan.Mods.Where(p => !p.Shared).Select(p => p.Manifest.Name).FirstOrDefault()
				?? plan.Mods.Select(p => p.Manifest.Name).FirstOrDefault()
				?? CleanName(Path.GetFileNameWithoutExtension(source.TrimEnd('\\', '/')));
		}
		catch
		{
			plan.Dispose();
			throw;
		}
		if (plan.Mods.Count == 0 && plan.Overlays.Count == 0)
		{
			plan.Dispose();
			throw new InvalidDataException($"Nothing to install in {Path.GetFileName(source.TrimEnd('\\', '/'))}: no folder with an Info.json, and no top folder named like an installed mod's folder.");
		}
		return plan;
	}

	/// <summary>
	/// One plan per mod of an extracted archive, for archives that are only a bundle of separate mods (Freeman's test
	/// zips): each becomes its own package, archived as a zip of its folder. Mods with nothing to copy (blocked, or
	/// shared and already installed) are left out. The plans use the archive's extracted folders: dispose the parent
	/// plan only after applying them.
	/// </summary>
	public static List<InstallPlan> Split(Settings settings, InstallPlan plan, ScanResult installed, StateStore state)
	{
		List<InstallPlan> plans = [];
		foreach (PlannedMod m in plan.Mods.Where(m => m.Copies))
		{
			InstallPlan sub = new() { SourcePath = m.Source, IsFolder = true, ExtractDir = m.Source, OwnsExtractDir = false };
			sub.Package = state.Match([m.Manifest],
				k => installed.Mods.FirstOrDefault(i => string.Equals(i.Key, k, StringComparison.OrdinalIgnoreCase))?.Manifest);
			sub.Mods.Add(PlanMod(settings, sub, m.Source, m.Manifest, installed, state));
			sub.Name = m.Manifest.Name;
			plans.Add(sub);
		}
		return plans;
	}

	private static PlannedMod PlanMod(Settings settings, InstallPlan plan, string root, ModManifest m, ScanResult installed, StateStore state)
	{
		InstalledMod? existing = installed.Mods.FirstOrDefault(i => string.Equals(i.Key, m.Key, StringComparison.OrdinalIgnoreCase));
		string folderName = root == plan.ExtractDir && !plan.IsFolder ? SafeFolderName(m.Name, CleanName(Path.GetFileNameWithoutExtension(plan.SourcePath))) : Path.GetFileName(root);
		string target = existing?.Folder ?? Path.Combine(settings.ModsDir, folderName);
		if (existing == null)
		{
			InstalledMod? occupant = installed.Mods.FirstOrDefault(i => string.Equals(i.Folder, target, StringComparison.OrdinalIgnoreCase));
			if (occupant != null)
			{
				return new PlannedMod
				{
					Manifest = m, Source = root, Target = target, Action = PlanAction.Blocked,
					Reason = $"the folder \"{folderName}\" already holds {occupant.Manifest.Name} ({occupant.Key})",
				};
			}
			return new PlannedMod { Manifest = m, Source = root, Target = target, Action = PlanAction.Install };
		}
		// Shared: some other installed package carries it too.
		bool shared = state.WithMember(m.Key).Any(p => p != plan.Package
			&& installed.Packages.Any(ip => ip.Record == p && ip.Members.Count > 0));
		int c = Versions.Compare(m.Version, existing.Manifest.Version);
		if (c == 0 && NewerBuild(root, existing.Folder) is DateTime built)
		{
			// Test builds often keep their version (Freeman's 1.0.0 packs): newer files under the same version are an update.
			return new PlannedMod
			{
				Manifest = m, Source = root, Target = target, Existing = existing, Shared = shared, Action = PlanAction.Update,
				Reason = $"same version, newer build ({built:yyyy-MM-dd HH:mm})",
			};
		}
		if (shared && c <= 0)
		{
			return new PlannedMod
			{
				Manifest = m, Source = root, Target = target, Existing = existing, Shared = true, Action = PlanAction.KeepInstalled,
				Reason = c == 0 ? "already installed at this version" : $"the installed {existing.Manifest.Version} is newer",
			};
		}
		return new PlannedMod
		{
			Manifest = m, Source = root, Target = target, Existing = existing, Shared = shared,
			Action = c > 0 ? PlanAction.Update : c < 0 ? PlanAction.Downgrade : PlanAction.Reinstall,
		};
	}

	/// <summary>
	/// When the source holds files that differ from (or are missing in) the installed folder and the newest of them is
	/// newer than the installed copies they would replace, that file's time; otherwise null.
	/// </summary>
	public static DateTime? NewerBuild(string source, string installed)
	{
		DateTime newestSource = DateTime.MinValue, newestInstalled = DateTime.MinValue;
		bool differs = false;
		foreach (string f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
		{
			FileInfo s = new(f), t = new(Path.Combine(installed, Path.GetRelativePath(source, f)));
			if (t.Exists && t.Length == s.Length)
			{
				continue;
			}
			differs = true;
			newestSource = s.LastWriteTime > newestSource ? s.LastWriteTime : newestSource;
			if (t.Exists && t.LastWriteTime > newestInstalled)
			{
				newestInstalled = t.LastWriteTime;
			}
		}
		return differs && newestSource > newestInstalled ? newestSource : null;
	}

	/// <summary>An archive without mods: top folders named like installed mods' folders (through wrapper folders) are add-ons.</summary>
	private static void PlanOverlays(InstallPlan plan, ScanResult installed)
	{
		string dir = plan.ExtractDir;
		for (int depth = 0; depth < 3; depth++)
		{
			foreach (string sub in Directory.EnumerateDirectories(dir))
			{
				InstalledMod? target = installed.Mods.FirstOrDefault(m => string.Equals(m.FolderName, Path.GetFileName(sub), StringComparison.OrdinalIgnoreCase));
				if (target != null)
				{
					plan.Overlays.Add(new PlannedOverlay
					{
						Target = target,
						Source = sub,
						Files = Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(sub, f)).ToList(),
					});
				}
			}
			string[] subs = Directory.GetDirectories(dir);
			if (plan.Overlays.Count > 0 || subs.Length != 1 || Directory.EnumerateFiles(dir).Any())
			{
				return;
			}
			dir = subs[0];
		}
	}

	/// <summary>
	/// Installs a plan: each copied mod's current folder is kept for rollback and the new files copied in (plain copies);
	/// Preferences.json gets the version (or a new enabled entry); add-on files go into their mods' folders; other
	/// packages' add-ons for a replaced mod are put back; the package is recorded and its archive filed.
	/// </summary>
	public static PackageRecord Apply(Settings settings, StateStore state, InstallPlan plan, NexusSource? nexus, Action<string> log)
	{
		Game.EnsureClosed(settings);
		Preferences prefs = Preferences.Load(settings.PreferencesPath);
		List<string> replaced = [];
		foreach (PlannedMod p in plan.Mods)
		{
			if (!p.Copies)
			{
				log($"  {p.Manifest.Name} {p.Manifest.Version}: {(p.Action == PlanAction.Blocked ? "skipped" : "left as it is")}, {p.Reason}");
				continue;
			}
			bool kept = false;
			if (Directory.Exists(p.Target))
			{
				KeepPrevious(p.Target, p.Manifest.Key);
				kept = true;
				replaced.Add(p.Manifest.Key);
			}
			Files.CopyDirectory(p.Source, p.Target);
			string what = p.Existing == null ? $"Installed {p.Manifest.Name} {p.Manifest.Version}" : $"{p.Action} {p.Manifest.Name} {p.Existing.Manifest.Version} -> {p.Manifest.Version}";
			log($"  {what} in \"{Path.GetFileName(p.Target)}\"" + (kept ? " (previous kept for rollback)" : ""));
			if (p.Manifest.Identifier is string id && prefs.SetVersionOrAdd(id, p.Manifest.Version, p.Manifest.Scope))
			{
				log($"    added {id} to Preferences.json, enabled");
			}
		}
		foreach (PlannedOverlay o in plan.Overlays)
		{
			CopyFiles(o.Source, o.Target.Folder, o.Files);
			log($"  Added {o.Files.Count} files to {o.Target.Manifest.Name} (\"{o.Target.FolderName}\")");
		}

		PackageRecord rec = plan.Package ?? new PackageRecord();
		if (plan.Package == null)
		{
			state.Packages.Add(rec);
		}
		rec.Members = plan.Mods.Where(p => p.Action != PlanAction.Blocked).Select(p => p.Manifest.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		rec.Overlays = plan.Overlays.ToDictionary(o => o.Target.Key, o => o.Files, StringComparer.OrdinalIgnoreCase);
		rec.Name = rec.IsAddOn || plan.NamedAfterArchive ? plan.Name : null;
		rec.Source = nexus != null ? "nxm" : plan.IsFolder ? "folder" : "zip";
		rec.SourceName = nexus?.FileName ?? Path.GetFileName(plan.SourcePath.TrimEnd('\\', '/'));
		rec.InstalledAt = DateTime.Now;
		if (nexus != null)
		{
			rec.NexusModId = nexus.ModId;
			rec.NexusFileId = nexus.FileId;
			rec.NexusFileName = nexus.FileName;
			rec.InstalledNexusVersion = nexus.FileVersion;
			rec.LinkSource = "nxm";
			if (rec.LatestFileId == nexus.FileId || (rec.LatestVersion != null && Versions.Compare(rec.LatestVersion, nexus.FileVersion) <= 0))
			{
				rec.LatestVersion = nexus.FileVersion;
				rec.LatestFileId = nexus.FileId;
			}
		}
		else
		{
			// A local archive: the Nexus page stays linked, but which file it is isn't known.
			rec.NexusFileId = null;
			rec.InstalledNexusVersion = null;
		}

		ReapplyAddOns(state, rec, replaced, plan, log);
		prefs.Save();
		string archived = ModArchive.Store(settings, state, rec, plan, nexus?.FileName);
		log($"  archive: {archived}");
		state.Save();
		return rec;
	}

	/// <summary>Other packages' add-on files for mods that were just replaced, copied in again from their archives.</summary>
	private static void ReapplyAddOns(StateStore state, PackageRecord current, List<string> replaced, InstallPlan plan, Action<string> log)
	{
		foreach (PackageRecord addOn in state.Packages.Where(p => p != current && p.Overlays.Keys.Any(k => replaced.Contains(k, StringComparer.OrdinalIgnoreCase))))
		{
			if (addOn.Archive == null || !File.Exists(addOn.Archive))
			{
				log($"  ! {addOn.Name ?? addOn.Id}'s files for a replaced mod can't be put back: its archive is missing");
				continue;
			}
			string tmp = Path.Combine(Settings.WorkDir, Guid.NewGuid().ToString("N")[..12]);
			try
			{
				Files.Extract(addOn.Archive, tmp);
				foreach ((string key, List<string> files) in addOn.Overlays.Where(kv => replaced.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)))
				{
					PlannedMod target = plan.Mods.First(m => string.Equals(m.Manifest.Key, key, StringComparison.OrdinalIgnoreCase));
					string folderName = Path.GetFileName(target.Target);
					string? src = Directory.EnumerateDirectories(tmp, "*", SearchOption.AllDirectories)
						.Where(d => string.Equals(Path.GetFileName(d), folderName, StringComparison.OrdinalIgnoreCase))
						.OrderBy(d => d.Length).FirstOrDefault();
					if (src == null)
					{
						log($"  ! {addOn.Name}: no \"{folderName}\" folder in its archive");
						continue;
					}
					CopyFiles(src, target.Target, files);
					log($"  Put back {addOn.Name}'s {files.Count} files in \"{folderName}\"");
				}
			}
			finally
			{
				Files.TryDeleteDirectory(tmp);
			}
		}
	}

	/// <summary>Puts back each mod's previous copy (the current one is kept instead, so it can be undone the same way).</summary>
	public static void RollBack(Settings settings, StateStore state, InstalledPackage package, Action<string> log)
	{
		Game.EnsureClosed(settings);
		List<InstalledMod> mods = package.Members.Where(m => HasPrevious(m.Key)).ToList();
		if (mods.Count == 0)
		{
			throw new InvalidOperationException($"No earlier version of {package.Name} is kept.");
		}
		Preferences prefs = Preferences.Load(settings.PreferencesPath);
		foreach (InstalledMod mod in mods)
		{
			string kept = PreviousPath(mod.Key);
			ModManifest back = ModManifest.ReadFolder(kept) ?? throw new InvalidDataException($"The kept copy of {mod.Manifest.Name} has no readable Info.json.");
			string swap = kept + ".swap";
			Files.MoveDirectory(mod.Folder, swap);
			Files.MoveDirectory(kept, mod.Folder);
			Files.MoveDirectory(swap, kept);
			if (back.Identifier is string id)
			{
				prefs.SetVersionOrAdd(id, back.Version, back.Scope);
			}
			log($"Rolled back {mod.Manifest.Name}: {mod.Manifest.Version} -> {back.Version}");
		}
		prefs.Save();
		if (package.Record != null)
		{
			package.Record.NexusFileId = null;
			package.Record.InstalledNexusVersion = null;
			state.Save();
		}
	}

	public static bool HasPrevious(string key) => Directory.Exists(PreviousPath(key));

	/// <summary>
	/// Uninstalls a package: its own mods move out of Mods (kept for rollback) and leave Preferences.json; libraries
	/// another installed package carries stay; its add-on files are deleted. The archive stays.
	/// </summary>
	public static void Uninstall(Settings settings, StateStore state, InstalledPackage package, Action<string> log)
	{
		Game.EnsureClosed(settings);
		Preferences prefs = Preferences.Load(settings.PreferencesPath);
		foreach (InstalledMod mod in package.Own)
		{
			KeepPrevious(mod.Folder, mod.Key);
			if (mod.Manifest.Identifier is string id)
			{
				prefs.Remove(id);
			}
			log($"Uninstalled {mod.Manifest.Name} {mod.Manifest.Version} (\"{mod.FolderName}\")");
		}
		foreach (InstalledMod mod in package.Members.Where(m => package.Shared.Contains(m.Key)))
		{
			log($"Kept {mod.Manifest.Name}: another installed package uses it");
		}
		if (package.Record != null)
		{
			ScanResult now = Scanner.Scan(settings, null, state);
			foreach ((string key, List<string> files) in package.Record.Overlays)
			{
				InstalledMod? target = now.Mods.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
				if (target == null)
				{
					continue;
				}
				foreach (string f in files)
				{
					string path = Path.Combine(target.Folder, f);
					if (File.Exists(path))
					{
						File.Delete(path);
					}
				}
				log($"Removed {files.Count} add-on files from \"{target.FolderName}\"");
			}
			state.Packages.Remove(package.Record);
		}
		prefs.Save();
		state.Save();
	}

	private static string PreviousPath(string key) =>
		Path.Combine(Settings.PreviousDir, string.Concat(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c)));

	private static void KeepPrevious(string folder, string key)
	{
		string dest = PreviousPath(key);
		Directory.CreateDirectory(Settings.PreviousDir);
		Files.TryDeleteDirectory(dest);
		Files.MoveDirectory(folder, dest);
	}

	private static void CopyFiles(string from, string to, List<string> files)
	{
		foreach (string f in files)
		{
			string dest = Path.Combine(to, f);
			Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
			File.Copy(Path.Combine(from, f), dest, true);
		}
	}

	public static string CleanName(string archiveName) =>
		System.Text.RegularExpressions.Regex.Replace(archiveName, @"[\s_-]+v?\d+(\.\d+)*[a-z]?(\s*\(.*\))?$", "").Trim();

	private static string SafeFolderName(string name, string fallback)
	{
		string s = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
		return s.Length > 0 ? s : fallback;
	}
}

/// <summary>The archive folder: one archive per package. A dropped folder is zipped into it.</summary>
public static class ModArchive
{
	public static string Store(Settings settings, StateStore state, PackageRecord rec, InstallPlan plan, string? name)
	{
		Directory.CreateDirectory(settings.ArchiveDir);
		string fileName = name ?? (plan.IsFolder
			? Safe($"{plan.Name} {plan.Mods.FirstOrDefault(m => !m.Shared)?.Manifest.Version ?? plan.Mods.FirstOrDefault()?.Manifest.Version}".Trim()) + ".zip"
			: Path.GetFileName(plan.SourcePath));
		string dest = FreeName(settings, state, rec, fileName);
		if (plan.IsFolder)
		{
			File.Delete(dest);
			ZipFile.CreateFromDirectory(plan.SourcePath, dest, CompressionLevel.Optimal, includeBaseDirectory: true);
		}
		else if (!string.Equals(Path.GetFullPath(plan.SourcePath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
		{
			File.Copy(plan.SourcePath, dest, true);
		}
		Replace(state, rec, dest);
		return dest;
	}

	/// <summary>The archive path for a file name: another package's archive with the same name stays, this one gets a numbered name.</summary>
	public static string FreeName(Settings settings, StateStore state, PackageRecord rec, string fileName)
	{
		string dest = Path.Combine(settings.ArchiveDir, fileName);
		for (int n = 2; state.Packages.Any(p => p != rec && string.Equals(p.Archive, dest, StringComparison.OrdinalIgnoreCase)); n++)
		{
			dest = Path.Combine(settings.ArchiveDir, $"{Path.GetFileNameWithoutExtension(fileName)} ({n}){Path.GetExtension(fileName)}");
		}
		return dest;
	}

	/// <summary>Points the package at its new archive and deletes the old one unless another package still uses it.</summary>
	public static void Replace(StateStore state, PackageRecord rec, string dest)
	{
		string? old = rec.Archive;
		rec.Archive = dest;
		if (old != null && !string.Equals(old, dest, StringComparison.OrdinalIgnoreCase)
			&& !state.Packages.Any(p => string.Equals(p.Archive, old, StringComparison.OrdinalIgnoreCase))
			&& File.Exists(old))
		{
			File.Delete(old);
		}
	}

	private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

/// <summary>File helpers: extraction (zip built in, 7z/rar through 7-Zip), mod roots, copies and moves.</summary>
public static class Files
{
	private const string SevenZip = @"C:\Program Files\7-Zip\7z.exe";

	public static void Extract(string archive, string dest)
	{
		Directory.CreateDirectory(dest);
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

	/// <summary>
	/// Folders holding an Info.json, at any depth (archives wrap mods in folders: LIRR GT-1's two mods sit two levels
	/// down). The game reads only Mods' top folders, so each one becomes its own folder there; nothing below a mod
	/// folder counts as another mod.
	/// </summary>
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

	public static void CopyDirectory(string from, string to)
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

	public static void MoveDirectory(string from, string to)
	{
		if (string.Equals(Path.GetPathRoot(Path.GetFullPath(from)), Path.GetPathRoot(Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase))
		{
			Directory.Move(from, to);
			return;
		}
		CopyDirectory(from, to);
		TryDeleteDirectory(from);
	}

	public static void TryDeleteDirectory(string dir)
	{
		if (!Directory.Exists(dir))
		{
			return;
		}
		foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
		{
			File.SetAttributes(f, FileAttributes.Normal);
		}
		Directory.Delete(dir, true);
	}
}

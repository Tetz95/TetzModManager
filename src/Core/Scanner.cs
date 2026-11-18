using System.Diagnostics;

namespace TetzModManager;

/// <summary>One installed mod folder, as the list shows it.</summary>
public sealed class InstalledMod
{
	public required ModManifest Manifest { get; init; }
	public required string Folder { get; init; }
	public string FolderName => Path.GetFileName(Folder);
	public string Key => Manifest.Key;
	/// <summary>Deployed by Vortex (it leaves a __folder_managed_by_vortex marker).</summary>
	public bool VortexManaged { get; init; }
	public bool HasManifest { get; init; } = true;
	public bool InPreferences { get; set; }
	/// <summary>Not in Preferences.json, but an enabled mod requires it, so the game loads it anyway.</summary>
	public bool LoadedAsDependency { get; set; }
	public bool? Enabled { get; set; }
	public string? PreferencesVersion { get; set; }
	public ModRecord? Record { get; set; }
	public List<string> Notes { get; } = [];

	/// <summary>The installed version as Nexus numbers it (authors don't always bump Info.json with the page).</summary>
	public string InstalledVersion => Record?.InstalledNexusVersion ?? Manifest.Version;

	public bool UpdateAvailable =>
		Record?.LatestVersion is string latest
		&& !(Record.LatestFileId != null && Record.LatestFileId == Record.NexusFileId)
		&& Versions.Compare(latest, InstalledVersion) > 0;
}

public static class Game
{
	public static bool IsRunning(Settings s) => Process.GetProcessesByName(s.GameProcess).Length > 0;

	public static void EnsureClosed(Settings s)
	{
		if (IsRunning(s))
		{
			throw new InvalidOperationException("Railroader is running: close it first (mods and Preferences.json can't be changed while it runs).");
		}
	}
}

public static class Scanner
{
	public const string VortexMarker = "__folder_managed_by_vortex";

	public static List<InstalledMod> Scan(Settings settings, Preferences? prefs, StateStore state)
	{
		List<InstalledMod> mods = [];
		if (!Directory.Exists(settings.ModsDir))
		{
			return mods;
		}
		foreach (string dir in Directory.EnumerateDirectories(settings.ModsDir).Order(StringComparer.OrdinalIgnoreCase))
		{
			ModManifest? m = ModManifest.ReadFolder(dir);
			bool vortex = File.Exists(Path.Combine(dir, VortexMarker));
			InstalledMod mod = m != null
				? new InstalledMod { Manifest = m, Folder = dir, VortexManaged = vortex }
				: new InstalledMod
				{
					Manifest = new ModManifest("folder:" + Path.GetFileName(dir), null, Path.GetFileName(dir), "", "", "", null, false, [], [], []),
					Folder = dir,
					VortexManaged = vortex,
					HasManifest = false,
				};
			if (!mod.HasManifest)
			{
				mod.Notes.Add(ModManifest.FindFile(dir) != null ? "Info.json can't be read" : "No Info.json");
			}
			if (prefs != null && m?.Identifier is string id)
			{
				mod.InPreferences = prefs.Find(id) != null;
				mod.Enabled = prefs.Enabled(id);
				mod.PreferencesVersion = prefs.Version(id);
			}
			mod.Record = state.Find(mod.Key);
			mods.Add(mod);
		}
		AddNotes(mods);
		return mods;
	}

	/// <summary>Identifiers the enabled mods require, directly or through other mods: the game loads those without entries.</summary>
	private static HashSet<string> NeededByEnabled(List<InstalledMod> mods, Dictionary<string, InstalledMod> byIdentifier)
	{
		HashSet<string> needed = new(StringComparer.OrdinalIgnoreCase);
		Queue<InstalledMod> queue = new(mods.Where(m => m.Enabled == true));
		while (queue.Count > 0)
		{
			foreach (string dep in queue.Dequeue().Manifest.Required)
			{
				string id = ModManifest.SplitDependency(dep).Id;
				if (needed.Add(id) && byIdentifier.TryGetValue(id, out InstalledMod? d))
				{
					queue.Enqueue(d);
				}
			}
		}
		return needed;
	}

	private static void AddNotes(List<InstalledMod> mods)
	{
		Dictionary<string, InstalledMod> byIdentifier = new(StringComparer.OrdinalIgnoreCase);
		foreach (InstalledMod mod in mods)
		{
			if (mod.Manifest.Identifier is string id)
			{
				byIdentifier.TryAdd(id, mod);
			}
		}

		// The same mod twice, under different folder names.
		foreach (IGrouping<string, InstalledMod> g in mods.Where(m => m.HasManifest).GroupBy(m => m.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
		{
			foreach (InstalledMod mod in g)
			{
				string others = string.Join(", ", g.Where(o => o != mod).Select(o => $"\"{o.FolderName}\" ({o.Manifest.Version})"));
				mod.Notes.Add($"Duplicate: same mod also in {others}");
			}
		}

		// Two mods installed from the same Nexus file (an author renamed the identifier, or a copy under another id).
		foreach (IGrouping<int, InstalledMod> g in mods.Where(m => m.Record?.NexusFileId != null)
			.GroupBy(m => m.Record!.NexusFileId!.Value).Where(g => g.Select(m => m.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
		{
			foreach (InstalledMod mod in g)
			{
				string others = string.Join(", ", g.Where(o => !string.Equals(o.Key, mod.Key, StringComparison.OrdinalIgnoreCase)).Select(o => $"{o.Manifest.Name} ({o.Key})"));
				mod.Notes.Add($"Possible duplicate: same Nexus file as {others}");
			}
		}

		HashSet<string> needed = NeededByEnabled(mods, byIdentifier);
		foreach (InstalledMod mod in mods.Where(m => m.Manifest.Identifier != null))
		{
			if (!mod.InPreferences)
			{
				mod.LoadedAsDependency = needed.Contains(mod.Manifest.Identifier!);
				if (!mod.LoadedAsDependency)
				{
					mod.Notes.Add("Not in Preferences.json and nothing enabled needs it (the game won't load it)");
				}
			}
			else if (mod.PreferencesVersion != null && mod.PreferencesVersion != mod.Manifest.Version)
			{
				mod.Notes.Add($"Preferences.json says {mod.PreferencesVersion}");
			}
			foreach (string dep in mod.Manifest.Required)
			{
				(string depId, string constraint) = ModManifest.SplitDependency(dep);
				if (depId == "rr/game")
				{
					if (constraint.StartsWith("==", StringComparison.Ordinal))
					{
						mod.Notes.Add($"Pinned to game {constraint[2..].Trim()}");
					}
					continue;
				}
				if (depId.StartsWith("rr/", StringComparison.Ordinal))
				{
					continue;
				}
				if (!byIdentifier.TryGetValue(depId, out InstalledMod? d))
				{
					mod.Notes.Add($"Needs {depId} (not installed)");
				}
				else if (d.Enabled == false && mod.Enabled != false)
				{
					mod.Notes.Add($"Needs {d.Manifest.Name} (disabled)");
				}
			}
			foreach (string inc in mod.Manifest.Incompatible)
			{
				(string incId, _) = ModManifest.SplitDependency(inc);
				if (byIdentifier.TryGetValue(incId, out InstalledMod? other) && other.Enabled != false && mod.Enabled != false)
				{
					mod.Notes.Add($"Incompatible with {other.Manifest.Name} (both enabled)");
				}
			}
		}
		foreach (InstalledMod mod in mods.Where(m => m.VortexManaged))
		{
			mod.Notes.Add("Deployed by Vortex");
		}
	}
}

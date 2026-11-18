using System.Diagnostics;

namespace TetzModManager;

/// <summary>One installed mod folder.</summary>
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
	public List<string> Notes { get; } = [];
}

/// <summary>A package as installed: its record (null for a mod no package claims) and its mods in Mods.</summary>
public sealed class InstalledPackage
{
	public PackageRecord? Record { get; init; }
	public List<InstalledMod> Members { get; } = [];
	/// <summary>Members the record lists that aren't in Mods.</summary>
	public List<string> Missing { get; } = [];
	/// <summary>Members another installed package carries too (a bundled library).</summary>
	public HashSet<string> Shared { get; } = new(StringComparer.OrdinalIgnoreCase);
	public InstalledMod? Primary { get; set; }
	public List<string> Notes { get; } = [];

	public string Id => Record?.Id ?? "mod:" + Members[0].Key;
	public string Name => Record?.Name ?? Primary?.Manifest.Name ?? Record?.NexusFileName ?? "(empty package)";
	public string Version => Primary?.Manifest.Version ?? "";
	public string Author => Primary?.Manifest.Author ?? "";

	/// <summary>The installed version as Nexus numbers it.</summary>
	public string InstalledVersion => Record?.InstalledNexusVersion ?? Version;

	public bool UpdateAvailable =>
		Record?.LatestVersion is string latest
		&& !(Record.LatestFileId != null && Record.LatestFileId == Record.NexusFileId)
		&& Versions.Compare(latest, InstalledVersion) > 0;

	/// <summary>Members the package controls on its own (not bundled libraries others share).</summary>
	public IEnumerable<InstalledMod> Own => Members.Where(m => !Shared.Contains(m.Key));
}

public sealed record ScanResult(List<InstalledMod> Mods, List<InstalledPackage> Packages);

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

	public static List<InstalledMod> ScanMods(Settings settings, Preferences? prefs)
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
			mods.Add(mod);
		}
		AddModNotes(mods);
		return mods;
	}

	public static ScanResult Scan(Settings settings, Preferences? prefs, StateStore state)
	{
		List<InstalledMod> mods = ScanMods(settings, prefs);
		return new ScanResult(mods, BuildPackages(mods, state));
	}

	public static List<InstalledPackage> BuildPackages(List<InstalledMod> mods, StateStore state)
	{
		Dictionary<string, InstalledMod> byKey = mods.GroupBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
		List<InstalledPackage> packages = [];
		HashSet<string> claimed = new(StringComparer.OrdinalIgnoreCase);
		foreach (PackageRecord r in state.Packages)
		{
			InstalledPackage p = new() { Record = r };
			foreach (string key in r.Members)
			{
				if (byKey.TryGetValue(key, out InstalledMod? m))
				{
					p.Members.Add(m);
					claimed.Add(key);
				}
				else
				{
					p.Missing.Add(key);
				}
			}
			packages.Add(p);
		}
		// Shared: a member another package with installed members carries too.
		foreach (InstalledPackage p in packages)
		{
			foreach (InstalledMod m in p.Members)
			{
				if (packages.Any(o => o != p && o.Members.Contains(m)))
				{
					p.Shared.Add(m.Key);
				}
			}
		}
		foreach (InstalledMod m in mods.Where(m => !claimed.Contains(m.Key)))
		{
			InstalledPackage p = new();
			p.Members.Add(m);
			packages.Add(p);
		}
		foreach (InstalledPackage p in packages)
		{
			p.Primary = ChoosePrimary(p);
			AddPackageNotes(p, byKey);
		}
		return packages;
	}

	/// <summary>
	/// The mod a package is named after: one of its own mods (not a bundled library), the one most of its other mods
	/// require (SCOLD's scold), else the one whose name is closest to the archive's.
	/// </summary>
	private static InstalledMod? ChoosePrimary(InstalledPackage p)
	{
		List<InstalledMod> own = p.Own.ToList();
		List<InstalledMod> pool = own.Count > 0 ? own : p.Members;
		if (pool.Count <= 1)
		{
			return pool.FirstOrDefault();
		}
		string hint = Normalize(p.Record?.SourceName ?? p.Record?.NexusFileName ?? Path.GetFileNameWithoutExtension(p.Record?.Archive ?? ""));
		return pool
			.OrderByDescending(m => Math.Max(Similarity(Normalize(m.Manifest.Name), hint), Similarity(Normalize(m.FolderName), hint)))
			.ThenByDescending(m => pool.Count(o => o != m && o.Manifest.Required.Any(d => string.Equals(ModManifest.SplitDependency(d).Id, m.Manifest.Identifier, StringComparison.OrdinalIgnoreCase))))
			.ThenBy(m => m.Manifest.Name.Length)
			.First();
	}

	private static string Normalize(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

	/// <summary>How well a mod's name matches the archive's: "GP40" in "Experimental Only GP40", "SCOLD LOAD ID" vs "SCOLD 1.0.8".</summary>
	private static int Similarity(string name, string hint)
	{
		if (name.Length == 0 || hint.Length == 0)
		{
			return 0;
		}
		if (hint.Contains(name))
		{
			return name.Length;
		}
		if (name.Contains(hint))
		{
			return hint.Length;
		}
		return CommonPrefix(name, hint);
	}

	private static int CommonPrefix(string a, string b)
	{
		int n = 0;
		while (n < a.Length && n < b.Length && a[n] == b[n])
		{
			n++;
		}
		return n;
	}

	private static void AddPackageNotes(InstalledPackage p, Dictionary<string, InstalledMod> byKey)
	{
		bool many = p.Members.Count > 1;
		foreach (InstalledMod m in p.Members)
		{
			foreach (string n in m.Notes)
			{
				p.Notes.Add(many ? $"{m.Manifest.Name}: {n}" : n);
			}
		}
		foreach (string key in p.Missing)
		{
			p.Notes.Add($"{key} is part of this package but not in Mods");
		}
		if (p.Record != null)
		{
			foreach (string key in p.Record.Overlays.Keys.Where(k => !byKey.ContainsKey(k)))
			{
				p.Notes.Add($"Add-on for {key}, which isn't installed");
			}
		}
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

	private static void AddModNotes(List<InstalledMod> mods)
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
	}
}

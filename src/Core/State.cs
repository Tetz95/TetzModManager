using System.Text.Json;

namespace TetzModManager;

/// <summary>
/// A package: what one archive (or dropped folder) installed. Its members are the mod keys in it (one archive can hold
/// several mods: SCOLD's three folders, BMAN's packs with his GP38Scripts). A mod can be a member of several packages
/// (a shared library bundled with each). Add-on packages have no mods of their own: their files go into other mods'
/// folders (Overlays). A package keeps its identity across versions: a new archive updates the installed package
/// whose own (unshared) mods it carries.
/// </summary>
public sealed class PackageRecord
{
	public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
	/// <summary>Mod keys this package carries.</summary>
	public List<string> Members { get; set; } = [];
	/// <summary>Add-on files: mod key of the folder they go into -> their paths relative to it.</summary>
	public Dictionary<string, List<string>> Overlays { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	/// <summary>Display name for an add-on package (others are named after their main mod).</summary>
	public string? Name { get; set; }

	public string? Archive { get; set; }
	/// <summary>The name of the archive it was installed from (names the package: "SD38-2 and GP38Scripts" -> SD38-2).</summary>
	public string? SourceName { get; set; }
	/// <summary>"zip", "folder", "nxm", "vortex" (imported from Vortex's downloads).</summary>
	public string? Source { get; set; }
	public DateTime? InstalledAt { get; set; }

	public int? NexusModId { get; set; }
	public int? NexusFileId { get; set; }
	public string? NexusFileName { get; set; }
	/// <summary>The Nexus file version of the installed copy (authors' Info.json versions often differ from it).</summary>
	public string? InstalledNexusVersion { get; set; }
	/// <summary>"nxm", "vortex", "manual".</summary>
	public string? LinkSource { get; set; }
	public string? LatestVersion { get; set; }
	public int? LatestFileId { get; set; }
	public DateTime? CheckedAt { get; set; }
	/// <summary>The installed files are newer than the page's newest file (a local or test build): no update offered.</summary>
	public bool NewerThanNexus { get; set; }
	/// <summary>Whether the latest file was uploaded after the installed one (null: unknown, versions decide).</summary>
	public bool? LatestIsNewer { get; set; }

	public bool IsAddOn => Members.Count == 0 && Overlays.Count > 0;
}

/// <summary>%APPDATA%\TetzModManager\state.json.</summary>
public sealed class StateStore
{
	public int Version { get; set; } = 2;
	public List<PackageRecord> Packages { get; set; } = [];

	private static string FilePath => Path.Combine(Settings.DataDir, "state.json");

	public PackageRecord? Find(string id) => Packages.FirstOrDefault(p => p.Id == id);

	/// <summary>Packages carrying this mod.</summary>
	public IEnumerable<PackageRecord> WithMember(string key) =>
		Packages.Where(p => p.Members.Contains(key, StringComparer.OrdinalIgnoreCase));

	/// <summary>
	/// The installed package these mods update. Packages are compared by their core: the mods no other mod in the same
	/// package requires. Bundled parts (BMAN's GP38Scripts and Blomberg trucks, in his GP38, GP40 and SD38-2 packs) don't
	/// make two packs one; SCOLD's core is its two aggregate packs. The cores must overlap by at least half of both
	/// together; packages known to come from another Nexus page never match.
	/// </summary>
	public PackageRecord? Match(IReadOnlyCollection<ModManifest> incoming, Func<string, ModManifest?> installed, int? nexusModId = null)
	{
		HashSet<string> core = Core(incoming);
		return Packages
			.Where(p => p.Members.Count > 0 && (nexusModId == null || p.NexusModId == null || p.NexusModId == nexusModId))
			.Select(p => (p, Score: Overlap(Core(p.Members.Select(k => installed(k) ?? Stub(k)).ToList()), core)))
			.Where(x => x.Score >= 0.5)
			.OrderByDescending(x => x.Score)
			.Select(x => x.p)
			.FirstOrDefault();
	}

	/// <summary>The mods of a set that no other mod of the set requires (all of them when every one is required).</summary>
	public static HashSet<string> Core(IReadOnlyCollection<ModManifest> mods)
	{
		HashSet<string> required = new(mods.SelectMany(m => m.Required.Select(d => ModManifest.SplitDependency(d).Id)), StringComparer.OrdinalIgnoreCase);
		List<string> core = mods.Where(m => m.Identifier == null || !required.Contains(m.Identifier)).Select(m => m.Key).ToList();
		return new HashSet<string>(core.Count > 0 ? core : mods.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);
	}

	private static ModManifest Stub(string key) => new(key, key, key, "", "", "", null, false, [], [], []);

	private static double Overlap(HashSet<string> a, HashSet<string> b)
	{
		if (a.Count == 0 || b.Count == 0)
		{
			return 0;
		}
		int both = a.Count(b.Contains);
		return (double)both / (a.Count + b.Count - both);
	}

	public static StateStore Load()
	{
		try
		{
			if (File.Exists(FilePath))
			{
				StateStore? s = JsonSerializer.Deserialize<StateStore>(File.ReadAllText(FilePath), Settings.Json);
				if (s is { Version: >= 2 })
				{
					foreach (PackageRecord p in s.Packages)
					{
						p.Overlays = new Dictionary<string, List<string>>(p.Overlays, StringComparer.OrdinalIgnoreCase);
					}
					return s;
				}
			}
		}
		catch (JsonException)
		{
		}
		return new StateStore();
	}

	public void Save()
	{
		Directory.CreateDirectory(Settings.DataDir);
		string tmp = FilePath + ".tmp";
		File.WriteAllText(tmp, JsonSerializer.Serialize(this, Settings.Json));
		File.Move(tmp, FilePath, true);
	}
}

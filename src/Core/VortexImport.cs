using System.IO.Compression;
using System.Text.RegularExpressions;

namespace TetzModManager;

/// <summary>
/// Links installed mods to their Nexus pages from Vortex's download folder. Vortex names a Nexus download
/// "&lt;name&gt; &lt;mod id&gt; &lt;version&gt; &lt;yyyy-mm-ddThh-mmZ&gt; &lt;id&gt;.zip"; the mod key comes from the Info.json inside, so
/// the archive's name never decides which mod it is.
/// </summary>
public static partial class VortexImport
{
	[GeneratedRegex(@"^(?<name>.+) (?<mod>\d+) (?<ver>\S+) (?<ts>\d{4}-\d\d-\d\dT\d\d-\d\dZ) (?<rnd>\w+)\.zip$", RegexOptions.IgnoreCase)]
	private static partial Regex VortexName();

	public static int Run(Settings settings, StateStore state, IReadOnlyList<InstalledMod> installed, Action<string> log)
	{
		if (!Directory.Exists(settings.VortexDownloadsDir))
		{
			log($"No Vortex download folder at {settings.VortexDownloadsDir}.");
			return 0;
		}
		HashSet<string> installedKeys = new(installed.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);
		// Newest download first, so each mod gets the page of its latest download.
		var downloads = Directory.EnumerateFiles(settings.VortexDownloadsDir, "*.zip")
			.Select(f => (Path: f, Match: VortexName().Match(Path.GetFileName(f))))
			.Where(x => x.Match.Success)
			.OrderByDescending(x => x.Match.Groups["ts"].Value)
			.ToList();
		Dictionary<string, InstalledMod> byKey = installed.GroupBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
		int linked = 0;
		HashSet<string> linkedKeys = new(StringComparer.OrdinalIgnoreCase), versioned = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string path, Match match) in downloads)
		{
			int modId = int.Parse(match.Groups["mod"].Value);
			string nexusVersion = match.Groups["ver"].Value;
			foreach (ModManifest m in Manifests(path))
			{
				if (!byKey.TryGetValue(m.Key, out InstalledMod? mod))
				{
					continue;
				}
				ModRecord r = state.Get(m.Key);
				// The newest download overall gives the page.
				if (linkedKeys.Add(m.Key) && r.NexusModId != modId && !(r.NexusModId != null && r.LinkSource is "nxm" or "manual"))
				{
					r.NexusModId = modId;
					r.NexusFileId = null;
					r.LinkSource = "vortex";
					linked++;
					log($"Linked {m.Name} ({m.Key}) to Nexus mod {modId}");
				}
				// The newest download of the installed version gives its Nexus version.
				if (r.NexusModId == modId && Versions.Same(m.Version, mod.Manifest.Version) && versioned.Add(m.Key) && r.LinkSource != "nxm")
				{
					r.InstalledNexusVersion = Versions.Same(nexusVersion, m.Version) ? null : nexusVersion;
				}
			}
		}
		state.Save();
		return linked;
	}

	/// <summary>The manifests of the mods inside a zip (each Info.json, shallowest per branch).</summary>
	public static List<ModManifest> Manifests(string zipPath)
	{
		List<ModManifest> list = [];
		try
		{
			using ZipArchive zip = ZipFile.OpenRead(zipPath);
			List<ZipArchiveEntry> infos = zip.Entries
				.Where(e => string.Equals(Path.GetFileName(e.FullName.Replace('\\', '/')), "info.json", StringComparison.OrdinalIgnoreCase))
				.OrderBy(e => e.FullName.Count(c => c is '/' or '\\'))
				.ToList();
			List<string> roots = [];
			foreach (ZipArchiveEntry e in infos)
			{
				string dir = Path.GetDirectoryName(e.FullName.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
				if (roots.Any(r => r.Length == 0 || dir.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase)))
				{
					continue;
				}
				roots.Add(dir);
				using StreamReader reader = new(e.Open());
				ModManifest? m = ModManifest.Parse(reader.ReadToEnd(), dir.Length > 0 ? Path.GetFileName(dir) : Path.GetFileNameWithoutExtension(zipPath));
				if (m != null)
				{
					list.Add(m);
				}
			}
		}
		catch (Exception e) when (e is InvalidDataException or IOException or System.Text.Json.JsonException)
		{
		}
		return list;
	}
}

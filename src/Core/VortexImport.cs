using System.IO.Compression;
using System.Text.RegularExpressions;

namespace TetzModManager;

/// <summary>
/// Builds packages for the installed mods from Vortex's download folder. Vortex names a Nexus download
/// "&lt;name&gt; &lt;mod id&gt; &lt;version&gt; &lt;yyyy-mm-ddThh-mmZ&gt; &lt;id&gt;.zip". Each installed mod belongs to the newest
/// download holding it (read from the Info.json files inside, never from the names), so SCOLD's three folders become
/// one package and GP38Scripts belongs to each of BMAN's packs. A download without mods whose top folder is an installed
/// mod's folder (Freeman's placards for RexsToolBox) becomes an add-on package.
/// </summary>
public static partial class VortexImport
{
	[GeneratedRegex(@"^(?<name>.+) (?<mod>\d+) (?<ver>\S+) (?<ts>\d{4}-\d\d-\d\dT\d\d-\d\dZ) (?<rnd>\w+)\.zip$", RegexOptions.IgnoreCase)]
	private static partial Regex VortexName();

	private sealed record Download(string Path, string Name, int? ModId, string? NexusVersion, string Stamp, List<ModManifest> Mods, Dictionary<string, List<string>> Overlays);

	public static (int Packages, int Archived) Run(Settings settings, StateStore state, ScanResult installed, bool copyArchives, Action<string> log)
	{
		if (!Directory.Exists(settings.VortexDownloadsDir))
		{
			log($"No Vortex download folder at {settings.VortexDownloadsDir}.");
			return (0, 0);
		}
		Dictionary<string, InstalledMod> byKey = installed.Mods.GroupBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
		Dictionary<string, InstalledMod> byFolder = installed.Mods.ToDictionary(m => m.FolderName, StringComparer.OrdinalIgnoreCase);

		List<Download> downloads = [];
		foreach (string f in Directory.EnumerateFiles(settings.VortexDownloadsDir).Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
		{
			Match m = VortexName().Match(Path.GetFileName(f));
			(List<ModManifest> mods, Dictionary<string, List<string>> overlays) = Read(f, byFolder);
			downloads.Add(new Download(f,
				m.Success ? m.Groups["name"].Value : Path.GetFileNameWithoutExtension(f),
				m.Success ? int.Parse(m.Groups["mod"].Value) : null,
				m.Success ? m.Groups["ver"].Value : null,
				m.Success ? m.Groups["ts"].Value : File.GetLastWriteTimeUtc(f).ToString("yyyy-MM-ddTHH-mmZ"),
				mods, overlays));
		}
		downloads = downloads.OrderByDescending(d => d.Stamp, StringComparer.Ordinal).ToList();

		// Each installed mod's newest download; those downloads are the packages.
		Dictionary<string, Download> newest = new(StringComparer.OrdinalIgnoreCase);
		foreach (Download d in downloads)
		{
			foreach (ModManifest m in d.Mods.Where(m => byKey.ContainsKey(m.Key)))
			{
				newest.TryAdd(m.Key, d);
			}
		}
		List<Download> packages = newest.Values.Distinct().ToList();
		// Add-ons: the newest download of each add-on name, when its target is installed.
		packages.AddRange(downloads.Where(d => d.Mods.Count == 0 && d.Overlays.Count > 0)
			.GroupBy(d => d.ModId?.ToString() ?? d.Name).Select(g => g.First()));

		int made = 0, archived = 0;
		foreach (Download d in packages)
		{
			List<string> members = d.Mods.Where(m => byKey.ContainsKey(m.Key)).Select(m => m.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			PackageRecord? rec = members.Count > 0
				? state.Match(d.Mods.Where(m => byKey.ContainsKey(m.Key)).ToList(), k => byKey.TryGetValue(k, out InstalledMod? im) ? im.Manifest : null, d.ModId)
				: state.Packages.FirstOrDefault(p => p.IsAddOn && (p.NexusModId == d.ModId && d.ModId != null || p.Name == d.Name));
			if (rec == null)
			{
				rec = new PackageRecord { Source = "vortex" };
				state.Packages.Add(rec);
				made++;
			}
			else if (rec.LinkSource is "nxm" or "manual")
			{
				// Installed or linked here since: this tool knows better than Vortex's old downloads.
				continue;
			}
			rec.Members = members;
			rec.Overlays = new Dictionary<string, List<string>>(d.Overlays, StringComparer.OrdinalIgnoreCase);
			rec.Name = rec.IsAddOn ? d.Name : null;
			rec.SourceName = d.Name;
			rec.NexusModId = d.ModId;
			rec.LinkSource = d.ModId != null ? "vortex" : null;
			bool matches = d.Mods.Where(m => byKey.ContainsKey(m.Key)).All(m => Versions.Same(m.Version, byKey[m.Key].Manifest.Version));
			rec.InstalledNexusVersion = matches ? d.NexusVersion : null;
			rec.NexusFileId = null;
			string label = rec.IsAddOn ? $"add-on {d.Name} for {string.Join(", ", d.Overlays.Keys)}" : string.Join(", ", members.Select(k => byKey[k].Manifest.Name));
			log($"Package from \"{Path.GetFileName(d.Path)}\": {label}" + (matches ? "" : " (installed versions differ from this download)"));
			if (copyArchives && matches)
			{
				Directory.CreateDirectory(settings.ArchiveDir);
				string name = d.NexusVersion != null && !d.Name.EndsWith(d.NexusVersion, StringComparison.OrdinalIgnoreCase) ? $"{d.Name} {d.NexusVersion}" : d.Name;
				// Two downloads can share a name (two "Colourable 1.0.1" packs): never point a package at another's archive.
				string dest = ModArchive.FreeName(settings, state, rec, string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".zip");
				if (!File.Exists(dest) || new FileInfo(dest).Length != new FileInfo(d.Path).Length)
				{
					File.Copy(d.Path, dest, true);
				}
				ModArchive.Replace(state, rec, dest);
				archived++;
			}
		}
		state.Save();
		return (made, archived);
	}

	/// <summary>The mods in a zip (each Info.json, shallowest per branch); without any, add-on files for installed folders.</summary>
	private static (List<ModManifest>, Dictionary<string, List<string>>) Read(string zipPath, Dictionary<string, InstalledMod> byFolder)
	{
		List<ModManifest> mods = [];
		Dictionary<string, List<string>> overlays = new(StringComparer.OrdinalIgnoreCase);
		try
		{
			using ZipArchive zip = ZipFile.OpenRead(zipPath);
			List<string> names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
			List<string> roots = [];
			foreach (ZipArchiveEntry e in zip.Entries
				.Where(e => string.Equals(Path.GetFileName(e.FullName.Replace('\\', '/')), "info.json", StringComparison.OrdinalIgnoreCase))
				.OrderBy(e => e.FullName.Count(c => c is '/' or '\\')))
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
					mods.Add(m);
				}
			}
			if (mods.Count == 0)
			{
				// Through up to two wrapper folders, a top folder named like an installed mod's folder.
				string prefix = "";
				for (int depth = 0; depth < 3 && overlays.Count == 0; depth++)
				{
					List<string> tops = names.Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && n.Length > prefix.Length)
						.Select(n => n[prefix.Length..].Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
					foreach (string top in tops)
					{
						if (byFolder.TryGetValue(top, out InstalledMod? target))
						{
							string p = prefix + top + "/";
							overlays[target.Key] = names.Where(n => n.StartsWith(p, StringComparison.OrdinalIgnoreCase) && !n.EndsWith('/'))
								.Select(n => n[p.Length..].Replace('/', Path.DirectorySeparatorChar)).ToList();
						}
					}
					if (tops.Count != 1)
					{
						break;
					}
					prefix += tops[0] + "/";
				}
			}
		}
		catch (Exception e) when (e is InvalidDataException or IOException or System.Text.Json.JsonException)
		{
		}
		return (mods, overlays);
	}
}

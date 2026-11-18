using TetzModManager;

// Smoke test. Reads the real home folder and Vortex downloads (never writes them); installs go into a throwaway home.
// Usage: set TETZMM_DATA=<scratch>; dotnet run -- <real home> <Vortex downloads dir> <LT zip new> <LT zip old> [nexus key file]
string data = Environment.GetEnvironmentVariable("TETZMM_DATA") ?? throw new Exception("Set TETZMM_DATA to a scratch folder.");
string realHome = args[0], vortex = args[1], ltNew = args[2], ltOld = args[3];
string? keyFile = args.Length > 4 ? args[4] : null;
void Log(string s) => Console.WriteLine(s);
int failures = 0;
void Check(bool ok, string what)
{
	Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
	if (!ok)
	{
		failures++;
	}
}
string Download(string startsWith) =>
	Directory.GetFiles(vortex, startsWith + "*").OrderByDescending(f => f).First();

// ---- 1. The real install, read only ----
Settings real = new() { HomeDir = realHome, VortexDownloadsDir = vortex, ArchiveDir = Path.Combine(data, "archive-real") };
string original = File.ReadAllText(real.PreferencesPath);
Check(Preferences.Load(real.PreferencesPath).Serialize() == original, "Preferences.json round trip is byte-identical");

StateStore state = new();
ScanResult scan = Scanner.Scan(real, Preferences.Load(real.PreferencesPath), state);
Log($"{scan.Mods.Count} mod folders");
(int made, _) = VortexImport.Run(real, state, scan, false, _ => { });
scan = Scanner.Scan(real, Preferences.Load(real.PreferencesPath), state);
Log($"Vortex import: {made} packages; {scan.Packages.Count} rows ({scan.Packages.Count(p => p.Record == null)} mods without a package)");
InstalledPackage? scold = scan.Packages.FirstOrDefault(p => p.Members.Any(m => m.Key == "scold/scold"));
Check(scold?.Members.Count == 3 && scold.Primary?.Key == "scold/scold", $"SCOLD is one package of 3 named after scold ({scold?.Name}: {string.Join(", ", scold?.Members.Select(m => m.FolderName) ?? [])})");
List<InstalledPackage> withScripts = scan.Packages.Where(p => p.Members.Any(m => m.FolderName == "GP38Scripts")).ToList();
Check(withScripts.Count >= 3 && withScripts.All(p => p.Shared.Contains(p.Members.First(m => m.FolderName == "GP38Scripts").Key) && p.Primary?.FolderName != "GP38Scripts"),
	$"GP38Scripts is shared by BMAN's packs, none named after it ({string.Join(" | ", withScripts.Select(p => p.Name))})");
InstalledPackage? placards = scan.Packages.FirstOrDefault(p => p.Record?.IsAddOn == true);
Check(placards != null, $"Freeman's placards are an add-on ({placards?.Name} -> {string.Join(", ", placards?.Record?.Overlays.Keys.ToList() ?? [])})");
InstalledPackage? lirr = scan.Packages.FirstOrDefault(p => p.Members.Count == 2 && p.Members.All(m => m.FolderName.StartsWith("LIRR")));
Check(lirr != null, "LIRR GT-1's two nested mods are one package");
Check(scan.Packages.Any(p => p.Members.Any(m => m.FolderName == "ld-gp38")) && scan.Packages.Any(p => p.Members.Any(m => m.FolderName == "ld-gp40"))
	&& scan.Packages.First(p => p.Members.Any(m => m.FolderName == "ld-gp38")) != scan.Packages.First(p => p.Members.Any(m => m.FolderName == "ld-gp40")),
	"BMAN's GP38 and GP40 packs stay two packages (they share GP38Scripts and the Blomberg trucks)");
InstalledPackage? gp60 = scan.Packages.FirstOrDefault(p => p.Members.Any(m => m.FolderName == "gp60"));
Check(gp60?.Members.Count == 3, $"Freeman's GP60 pack is one package of 3 ({gp60?.Name})");

if (keyFile != null)
{
	real.NexusKeyFile = keyFile;
	using NexusClient nexus = new(real.ReadNexusKey() ?? throw new Exception("no key"));
	int updates = await Updates.Check(nexus, state, scan.Packages, Log);
	int linked = scan.Packages.Count(p => p.Record?.NexusModId != null);
	Log($"{updates} updates; {scan.Packages.Count(p => p.Record?.NexusFileId != null)} of {linked} linked packages matched to their Nexus file");
	Check(scan.Packages.Count(p => p.Record?.NexusFileId != null) > linked / 2, "most linked packages matched to their Nexus file");
}

// ---- 2. Installs into a throwaway home ----
data = Path.Combine(data, "install-test");
Environment.SetEnvironmentVariable("TETZMM_DATA", data);
Files.TryDeleteDirectory(data);
string home = Path.Combine(data, "home");
Directory.CreateDirectory(Path.Combine(home, "Mods"));
File.Copy(real.PreferencesPath, Path.Combine(home, "Preferences.json"));
Settings fake = new() { HomeDir = home, ArchiveDir = Path.Combine(data, "archive"), GameProcess = "NoSuchProcess-TetzMM" };
StateStore st = new();
PackageRecord Install(string source)
{
	using InstallPlan plan = Installer.Plan(fake, source, Scanner.Scan(fake, null, st), st);
	Log($"[{Path.GetFileName(source)}] {string.Join(", ", plan.Mods.Select(m => $"{m.Manifest.Name}:{m.Action}"))}{string.Join(", ", plan.Overlays.Select(o => $"add-on for {o.Target.FolderName} ({o.Files.Count} files)"))}");
	return Installer.Apply(fake, st, plan, null, Log);
}
ScanResult Now() => Scanner.Scan(fake, Preferences.Load(fake.PreferencesPath), st);

// SCOLD: three folders, one package, Preferences entries for each.
Install(Download("SCOLD"));
Check(Now().Packages.Count == 1 && Now().Mods.Count == 3, "SCOLD installs three folders as one package");

// BMAN: GP38 pack, then SD38-2, both carrying GP38Scripts: whichever copy is newer ends up installed, never the older.
Install(Download("GP38 6.0.2"));
string scriptsBefore = Now().Mods.First(m => m.FolderName == "GP38Scripts").Manifest.Version;
string sd38Zip = Download("SD38-2");
string bundled;
using (System.IO.Compression.ZipArchive z = System.IO.Compression.ZipFile.OpenRead(sd38Zip))
{
	System.IO.Compression.ZipArchiveEntry e = z.Entries.First(x => x.FullName.Replace('\\', '/').EndsWith("GP38Scripts/Info.json", StringComparison.OrdinalIgnoreCase));
	using StreamReader r = new(e.Open());
	bundled = ModManifest.Parse(r.ReadToEnd(), "GP38Scripts")!.Version;
}
PackageRecord sd38 = Install(sd38Zip);
ScanResult s2 = Now();
string newest = Versions.Compare(bundled, scriptsBefore) > 0 ? bundled : scriptsBefore;
Check(s2.Mods.First(m => m.FolderName == "GP38Scripts").Manifest.Version == newest,
	$"GP38Scripts ends at the newer copy ({scriptsBefore} installed, {bundled} in SD38-2 -> {newest})");
Check(s2.Packages.Count(p => p.Shared.Count > 0) == 2, "GP38Scripts is shared by both BMAN packages");
Installer.Uninstall(fake, st, s2.Packages.First(p => p.Record == sd38), Log);
s2 = Now();
Check(s2.Mods.Any(m => m.FolderName == "GP38Scripts") && !s2.Mods.Any(m => m.FolderName == "SD382"), "uninstalling SD38-2 keeps GP38Scripts (GP38 still uses it)");

// LIRR GT-1: two mods two levels down become two top folders.
Install(Download("LIRR GT 1"));
Check(Now().Mods.Count(m => m.FolderName.StartsWith("LIRR GT-1")) == 2, "LIRR GT-1's nested mods install as two top folders");

// A folder install (an unzipped download), then an older zip of it, then rollback.
string unzipped = Path.Combine(data, "unzipped");
Files.Extract(ltNew, unzipped);
PackageRecord lt = Install(unzipped);
Check(lt.Archive != null && File.Exists(lt.Archive) && lt.Archive.EndsWith(".zip"), $"a folder install is archived as a zip ({Path.GetFileName(lt.Archive)})");
string ltVersion = Now().Mods.First(m => m.Key == "tetz95/diesel-traction").Manifest.Version;
Install(ltOld);
Check(st.Packages.Count(p => p.Members.Contains("tetz95/diesel-traction")) == 1, "an older zip of the same mod updates the same package");
Check(Directory.GetFiles(fake.ArchiveDir).Count(f => Path.GetFileName(f).Contains("Locomotive Traction") || Path.GetFileName(f).Contains("Diesel")) == 1, "one archive per package");
Installer.RollBack(fake, st, Now().Packages.First(p => p.Members.Any(m => m.Key == "tetz95/diesel-traction")), Log);
Check(Now().Mods.First(m => m.Key == "tetz95/diesel-traction").Manifest.Version == ltVersion, "rollback restores the newer version");

// An add-on: Freeman's placards into RexsToolBox, put back when RexsToolBox is reinstalled, removed with the add-on.
string rexReal = Path.Combine(real.ModsDir, "RexsToolBox");
string rexFake = Path.Combine(data, "rex-src", "RexsToolBox");
Directory.CreateDirectory(rexFake);
File.Copy(Path.Combine(rexReal, "Info.json"), Path.Combine(rexFake, "Info.json"));
File.WriteAllText(Path.Combine(rexFake, "placeholder.txt"), "stand-in for Rex's files");
Install(rexFake);
PackageRecord addOn = Install(Download("Freeman RTB Placards"));
string rexInstalled = Now().Mods.First(m => m.FolderName == "RexsToolBox").Folder;
string aPlacard = Path.Combine(rexInstalled, addOn.Overlays.Values.First().First());
Check(addOn.IsAddOn && File.Exists(aPlacard), $"the placards land in RexsToolBox ({addOn.Overlays.Values.First().Count} files)");
Install(rexFake);
Check(File.Exists(aPlacard), "reinstalling RexsToolBox puts the placards back");
Installer.Uninstall(fake, st, Now().Packages.First(p => p.Record == addOn), Log);
Check(!File.Exists(aPlacard) && File.Exists(Path.Combine(rexInstalled, "placeholder.txt")), "uninstalling the add-on removes only its files");

// The same mod under another folder name is flagged.
Files.CopyDirectory(Now().Mods.First(m => m.FolderName == "scold").Folder, Path.Combine(home, "Mods", "scold copy"));
Check(Now().Mods.Where(m => m.Key == "scold/scold").All(m => m.Notes.Any(n => n.StartsWith("Duplicate"))), "the same mod under two folder names is flagged on both");

Check(Preferences.Load(fake.PreferencesPath).Find("aggregate-production-loads") != null || Preferences.Load(fake.PreferencesPath).Serialize().Contains("scold/scold"), "Preferences.json has SCOLD's entries");
// Rollback copies taken from a OneDrive home keep read-only folders; replacing one must still work.
string ro = Path.Combine(data, "readonly-folders");
Directory.CreateDirectory(Path.Combine(ro, "Assets", "Inner"));
File.WriteAllText(Path.Combine(ro, "Assets", "Inner", "a.txt"), "x");
foreach (string d in new[] { Path.Combine(ro, "Assets", "Inner"), Path.Combine(ro, "Assets"), ro })
{
	new DirectoryInfo(d).Attributes |= FileAttributes.ReadOnly;
}
Files.TryDeleteDirectory(ro);
Check(!Directory.Exists(ro), "a folder tree with read-only folders is deleted");

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures;

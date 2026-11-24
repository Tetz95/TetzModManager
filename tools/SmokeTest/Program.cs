using TetzModManager;

// Smoke test. Reads the real home folder and Vortex downloads (never writes them); installs go into a throwaway home.
// Usage: set TETZMM_DATA=<scratch>; dotnet run -- <real home> <Vortex downloads dir> <LT zip new> <LT zip old> [online]
string data = Environment.GetEnvironmentVariable("TETZMM_DATA") ?? throw new Exception("Set TETZMM_DATA to a scratch folder.");
string realHome = args[0], vortex = args[1], ltNew = args[2], ltOld = args[3];
bool online = args.Length > 4 && args[4] == "online";
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
Check(Installer.CleanName("BSMS 6.0.4 504 1 2026-11-19T08-41Z UR6lEFE0n") == "BSMS", "Nexus download suffix (v3) is stripped from names");
Check(Installer.CleanName("Ties Load-1810-1-2-0-1731000000") == "Ties Load", "Nexus download suffix (v1) is stripped from names");
Check(Installer.CleanName("Freeman Test") == "Freeman Test" && Installer.CleanName("Tetz's Rails Load 1.2.0") == "Tetz's Rails Load", "plain archive names keep their name");

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

if (online)
{
	using NexusPublicClient nexus = new();
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

// A Nexus file with the same Info.json version but older files than an installed test build is an older build.
string buildA = Path.Combine(data, "build-installed"), buildB = Path.Combine(data, "build-nexus");
Directory.CreateDirectory(buildA);
Directory.CreateDirectory(buildB);
File.WriteAllText(Path.Combine(buildA, "a.dll"), "newer test build");
File.WriteAllText(Path.Combine(buildB, "a.dll"), "old");
File.SetLastWriteTime(Path.Combine(buildB, "a.dll"), DateTime.Now.AddDays(-30));
Check(Installer.OlderBuild(buildB, buildA) != null && Installer.NewerBuild(buildB, buildA) == null, "same version with older files is an older build");
File.SetLastWriteTime(Path.Combine(buildB, "a.dll"), new DateTime(1980, 1, 1));
Check(Installer.OlderBuild(buildB, buildA) == null, "files without times (1980) don't count as older");

// ---- 3. Which mods load, and why not the others (a made-up home) ----
Log($"game version: {Steam.GameVersion() ?? "not found"}");
Check(Steam.GameVersion() is string gv && System.Text.RegularExpressions.Regex.IsMatch(gv, @"^\d+\.\d+\.\d+[a-z]?$"), "the game's version is found");
foreach (InstalledMod m in scan.Mods.Where(m => m.NotLoaded != null))
{
	Log($"  {m.Manifest.Name}: {m.NotLoaded}");
}
Check(scan.Mods.Where(m => m.LoadedAsDependency).All(m => m.NotLoaded == null), "a mod loaded because another requires it has no reason against it");
string home3 = Path.Combine(data, "home3");
Settings rules = new() { HomeDir = home3, ArchiveDir = Path.Combine(data, "archive3"), GameProcess = "NoSuchProcess-TetzMM" };
StateStore st3 = new();
void Mod(string id, string version, string required = "", string incompatible = "", string extra = "")
{
	string dir = Path.Combine(home3, "Mods", id.Replace('/', '-'));
	Directory.CreateDirectory(dir);
	string List(string s) => string.Join(",", s.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => $"\"{x}\""));
	File.WriteAllText(Path.Combine(dir, "Info.json"),
		$$"""{ "identifier": "{{id}}", "name": "{{id[2..]}}", "version": "{{version}}", {{extra}} "dependencies": { "required": [{{List(required)}}], "incompatible": [{{List(incompatible)}}] } }""");
}
void Prefs(string entries, bool dll = true) => File.WriteAllText(Path.Combine(home3, "Preferences.json"),
	$$"""{ "mods": [{{string.Join(",", entries.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(e => $$"""{ "identifier": "{{e.TrimStart('-')}}", "version": "1.0.0", "enabled": {{(e.StartsWith('-') ? "false" : "true")}}, "scope": 0 }"""))}}], "settings": { "mods.allow-dll": {{(dll ? 1 : 0)}} } }""");
Preferences P3() => Preferences.Load(rules.PreferencesPath);
InstalledMod M(string id) => Scanner.Scan(rules, P3(), st3).Mods.First(m => m.Key == id);
bool Why(string id, string text) => M(id) is { Loads: false, NotLoaded: string why } && (why.Contains(text) || Say(why));
bool Say(string s)
{
	Log("  got: " + s);
	return false;
}
Mod("t/lib", "1.0.0");
Mod("t/main", "1.0.0", "rr/game;t/lib");
Mod("t/yard", "1.0.0", "t/main >=1.0.0");
Mod("t/off", "1.0.0");
Mod("t/never", "1.0.0");
Mod("t/missing", "1.0.0", "t/nothere");
Mod("t/old", "1.0.0", "t/main >=2.0.0");
Mod("t/chain", "1.0.0", "t/missing");
Mod("t/foe", "1.0.0", "", "t/main");
Mod("t/future", "1.0.0", "rr/game >=9.0.0");
Mod("t/range", "1.0.0", "rr/game >=0.6.0 <9.0.0;t/lib ^1.0.0;t/main ~1.0.0");
Mod("t/dll", "1.0.0", extra: "\"assemblies\": [\"a.dll\"],");
Mod("t/c1", "1.0.0", "t/c2");
Mod("t/c2", "1.0.0", "t/c1");
Mod("t/lone", "1.0.0", "t/foe");
Prefs("t/yard -t/off t/missing t/old t/chain t/foe t/future t/range t/dll t/c1", dll: false);
Check(M("t/main").LoadedAsDependency && M("t/lib").LoadedAsDependency && M("t/yard") is { Loads: true, LoadedAsDependency: false } && M("t/range").Loads,
	"required mods load without an entry; constraints that hold");
Check(M("t/main").RequiredBy.Contains("yard") && !M("t/main").RequiredBy.Contains("old"), "it says which loaded mods require it");
Check(Why("t/off", "Disabled in Preferences.json") && M("t/off").OffByChoice && M("t/off").Notes.Count == 0, "switched off: a reason, no warning");
Check(Why("t/never", "not in Preferences.json, and no mod requires it"), "never enabled");
Check(Why("t/missing", "needs t/nothere, which isn't installed"), "a missing requirement");
Check(Why("t/old", "needs main >=2.0.0 (1.0.0 installed)"), "a requirement that's too old");
Check(Why("t/chain", "needs missing, which can't load (needs t/nothere"), "a requirement that can't load");
Check(Why("t/foe", "it's incompatible with main"), "an incompatible mod");
Check(Why("t/future", "needs Railroader >=9.0.0"), "a newer game needed");
Check(Why("t/dll", "DLL mods are off"), "DLL mods off");
Check(Why("t/c1", "require each other") && Why("t/c2", "require each other"), "a circular requirement");
Check(Why("t/lone", "can't load") && M("t/lone").CannotLoad, "off, and it couldn't load if it were on");
Prefs("t/yard t/off", dll: true);
Installer.SetEnabled(rules, st3, [M("t/yard")], false, Log);
Check(P3().Enabled("t/main") == true && P3().Find("t/lib") == null && M("t/main").Loads && M("t/lib").LoadedAsDependency,
	"disabling the last mod that required one keeps it enabled (not what it requires itself)");
Prefs("t/yard t/range");
Installer.SetEnabled(rules, st3, [M("t/yard")], false, Log);
Check(P3().Find("t/main") == null && M("t/main").LoadedAsDependency, "no entry while another mod still requires it");
Prefs("-t/main");
Installer.SetEnabled(rules, st3, [M("t/never"), M("t/yard"), M("t/main")], true, Log);
Check(P3().Enabled("t/never") == true && P3().Enabled("t/yard") == true && P3().Enabled("t/main") == true && M("t/never").Loads, "enabling a mod without an entry adds one");
Prefs("t/yard");
Installer.Uninstall(rules, st3, Scanner.Scan(rules, P3(), st3).Packages.First(p => p.Primary?.Key == "t/yard"), Log);
Check(P3().Enabled("t/main") == true && P3().Find("t/yard") == null, "uninstalling the last mod that required one keeps it enabled");
// A library that came only inside another package goes off with it.
Mod("t/pack", "1.0.0", "t/packlib");
Mod("t/packlib", "1.0.0");
st3.Packages.Add(new PackageRecord { Members = ["t/pack", "t/packlib"], Name = null, Source = "zip", SourceName = "pack" });
Prefs("t/pack");
Installer.SetEnabled(rules, st3, [M("t/pack")], false, Log);
Check(P3().Find("t/packlib") == null && Why("t/packlib", "(which requires it) isn't loaded"), "a package's own library goes off with it");
// Updating a mod loaded as a dependency doesn't give it an entry.
Mod("t/yard", "1.0.0", "t/main >=1.0.0");
Prefs("t/yard");
string mainSrc = Path.Combine(data, "main-src", "t-main");
Files.CopyDirectory(Path.Combine(home3, "Mods", "t-main"), mainSrc);
File.WriteAllText(Path.Combine(mainSrc, "Info.json"), File.ReadAllText(Path.Combine(mainSrc, "Info.json")).Replace("\"version\": \"1.0.0\"", "\"version\": \"1.1.0\""));
using (InstallPlan plan3 = Installer.Plan(rules, mainSrc, Scanner.Scan(rules, P3(), st3), st3))
{
	Installer.Apply(rules, st3, plan3, null, Log);
}
Check(M("t/main") is { Loads: true, InPreferences: false } && M("t/main").Manifest.Version == "1.1.0", "updating a mod loaded as a dependency adds no entry");

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures;

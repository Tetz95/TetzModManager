using TetzModManager;

// Smoke test. Reads the real home folder (never writes it); installs go into a throwaway home under TETZMM_DATA.
// Usage: set TETZMM_DATA=<scratch dir>; dotnet run -- <real home> <a zip of an installed mod> <an older zip of it> [nexus key file]
string data = Environment.GetEnvironmentVariable("TETZMM_DATA") ?? throw new Exception("Set TETZMM_DATA to a scratch folder.");
string realHome = args[0], zipNew = args[1], zipOld = args[2];
string? keyFile = args.Length > 3 ? args[3] : null;
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

// 1. Preferences.json round trip: unchanged data must write back byte for byte.
Settings real = new() { HomeDir = realHome };
string original = File.ReadAllText(real.PreferencesPath);
Check(Preferences.Load(real.PreferencesPath).Serialize() == original, "Preferences.json round trip is byte-identical");

// 2. Scan the real Mods folder.
StateStore state = new();
List<InstalledMod> mods = Scanner.Scan(real, Preferences.Load(real.PreferencesPath), state);
Log($"{mods.Count} folders, {mods.Count(m => m.Manifest.Identifier != null)} 0.6 mods, {mods.Count(m => m.Manifest.Umm)} UMM manifests, {mods.Count(m => !m.HasManifest)} without");
foreach (var g in mods.SelectMany(m => m.Notes.Select(n => (m, n))).Where(x => x.n != "Deployed by Vortex").GroupBy(x => x.n.Split(':')[0].Split(" (")[0]))
{
	Log($"  note '{g.Key}': {g.Count()}  e.g. {g.First().m.Manifest.Name}: {g.First().n}");
}
Check(mods.Count > 50, "scan found the installed mods");

// 3. Vortex links (into the scratch state).
int linked = VortexImport.Run(real, state, mods, _ => { });
Log($"Vortex import linked {linked} mods to Nexus pages");
Check(linked > 20, "Vortex downloads link most Nexus mods");

// 4. Update check against Nexus.
if (keyFile != null)
{
	real.NexusKeyFile = keyFile;
	using NexusClient nexus = new(real.ReadNexusKey() ?? throw new Exception("no key"));
	mods = Scanner.Scan(real, Preferences.Load(real.PreferencesPath), state);
	int updates = await Updates.Check(nexus, state, mods, Log);
	int resolved = mods.Count(m => m.Record?.NexusFileId != null);
	Log($"{updates} updates; {resolved} of {mods.Count(m => m.Record?.NexusModId != null)} linked mods matched to their Nexus file");
	Check(resolved > 0, "installed versions matched to Nexus files");
}

// 5. Installs into a throwaway home: same version, older version, rollback, uninstall, archive.
// Its own data folder, so the links and update results above stay in <data>\state.json.
data = Path.Combine(data, "install-test");
Environment.SetEnvironmentVariable("TETZMM_DATA", data);
string home = Path.Combine(data, "home");
if (Directory.Exists(home))
{
	Directory.Delete(home, true);
}
Directory.CreateDirectory(Path.Combine(home, "Mods"));
File.Copy(real.PreferencesPath, Path.Combine(home, "Preferences.json"));
Settings fake = new() { HomeDir = home, ArchiveDir = Path.Combine(data, "archive"), GameProcess = "NoSuchProcess-TetzMM" };
StateStore st = new();

using (InstallPlan plan = Installer.Plan(fake, zipNew, Scanner.Scan(fake, null, st)))
{
	Check(plan.Mods.Count == 1, $"plan finds one mod in {Path.GetFileName(zipNew)}");
	Installer.Apply(fake, st, plan, null, Log);
}
List<InstalledMod> now = Scanner.Scan(fake, Preferences.Load(fake.PreferencesPath), st);
InstalledMod lt = now.Single();
string id = lt.Manifest.Identifier!;
string newVersion = lt.Manifest.Version;
Check(Preferences.Load(fake.PreferencesPath).Version(id) == newVersion, $"Preferences.json has {id} {newVersion}");

// Dropping the old zip under a different folder name still lands in the same folder.
using (InstallPlan plan = Installer.Plan(fake, zipOld, now))
{
	Check(plan.Mods.Single().Target == lt.Folder, "an older zip targets the installed folder (same identifier)");
	Installer.Apply(fake, st, plan, null, Log);
}
now = Scanner.Scan(fake, Preferences.Load(fake.PreferencesPath), st);
string oldVersion = now.Single().Manifest.Version;
Check(oldVersion != newVersion && Preferences.Load(fake.PreferencesPath).Version(id) == oldVersion, $"downgraded to {oldVersion}, Preferences.json follows");
Check(Directory.GetFiles(fake.ArchiveDir).Length == 1, "archive keeps one zip per mod");
Check(Installer.HasPrevious(now.Single().Key), "previous version kept");

Installer.RollBack(fake, st, now.Single(), Log);
now = Scanner.Scan(fake, Preferences.Load(fake.PreferencesPath), st);
Check(now.Single().Manifest.Version == newVersion && Preferences.Load(fake.PreferencesPath).Version(id) == newVersion, "rollback restores the newer version");

Installer.Uninstall(fake, st, now.Single(), Log);
Check(Scanner.Scan(fake, null, st).Count == 0 && Preferences.Load(fake.PreferencesPath).Find(id) == null, "uninstall removes the folder and the Preferences entry");

// A duplicate: the same mod in a second folder.
using (InstallPlan plan = Installer.Plan(fake, zipNew, Scanner.Scan(fake, null, st)))
{
	Installer.Apply(fake, st, plan, null, Log);
}
string first = Scanner.Scan(fake, null, st).Single().Folder;
CopyAll(first, Path.Combine(home, "Mods", "Some Other Name"));
now = Scanner.Scan(fake, Preferences.Load(fake.PreferencesPath), st);
Check(now.All(m => m.Notes.Any(n => n.StartsWith("Duplicate"))), "the same mod under two folder names is flagged on both");

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures;

static void CopyAll(string from, string to)
{
	foreach (string d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
	{
		Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
	}
	Directory.CreateDirectory(to);
	foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
	{
		File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
	}
}

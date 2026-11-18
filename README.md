# Tetz's Mod Manager

A personal mod manager for Railroader 0.6 (2026.1): a Windows window (WPF, .NET 10) that installs mods into the game's
home folder, keeps Preferences.json in step, checks Nexus for updates and keeps every installed archive.

## What it does

- **The list comes from the mods themselves.** Each folder in `Mods` is read from its `Info.json` (identifier, name,
  version, author, dependencies), or an old Unity Mod Manager `info.json` (`Id`, `DisplayName`). A mod is known by its
  identifier, never by its folder name or the name of the archive it came in, so the same mod under two names is one
  mod. Nexus details (mod id, file id, newest version) are kept behind it, in `%APPDATA%\TetzModManager\state.json`.
- **Installs** from a zip (7z/rar through 7-Zip), dropped on the window, or from a Nexus "Mod Manager Download" link:
  - an installed mod is replaced in its own folder (whatever the archive calls its folder); a new one gets the
    archive's folder;
  - plain copies, no hardlinks; the folder being replaced is kept for **Roll back** (one earlier version per mod, in
    `%LOCALAPPDATA%\TetzModManager\previous`);
  - Preferences.json gets the new version, or a new entry (enabled, scope 0; client mods in `clientMods`). Everything
    else in the file is written back as it was (two-space indent, its own line endings); the last 20 versions of the
    file are kept in `%APPDATA%\TetzModManager\preferences-backups`;
  - nothing is changed while Railroader runs.
- **Archive**: every installed archive is copied to the archive folder (default `D:\Railroader Mod Archive`); the
  previous archive of the same mod is deleted, so there is one per mod.
- **Nexus**: "Link from Vortex downloads" links installed mods to their pages from Vortex's download names
  (`<name> <mod id> <version> <timestamp> <id>.zip`, the mod read from the Info.json inside). "Check for updates"
  matches each installed version to its Nexus file (by version, or by the Nexus version from the download name when the
  author's Info.json says something else) and follows Nexus's file-update chain to the newest file. "Update" downloads
  it with the API key (Premium: no nxm key needed).
- **nxm links** (Settings > "Handle Nexus links here"): Windows sends `nxm://` links here; links for other games go to
  the handler that was there before (Vortex), and "Give Nexus links back" restores it.
- **Notes** on each mod: the same mod in two folders, two identifiers from the same Nexus file, not in Preferences.json
  while nothing enabled needs it (libraries required by an enabled mod load without an entry), Preferences.json version
  differing from the folder's, a missing or disabled required mod, an enabled incompatible mod, a game-version pin,
  folders deployed by Vortex.

## Build, test, install

```bash
dotnet build TetzModManager.sln
# smoke test: reads the real home folder, installs into a throwaway one
TETZMM_DATA=<scratch> dotnet run --project tools/SmokeTest -- "<home>" "<zip of an installed mod>" "<older zip of it>" [key file]
# install to %LOCALAPPDATA%\Programs\TetzModManager (close it first; publish fails on a folder name with an apostrophe)
dotnet publish src/App/App.csproj -c Release -o "$LOCALAPPDATA/Programs/TetzModManager"
```

`TETZMM_DATA` moves all of the tool's own files (settings, state, rollback copies, work folder) for tests;
`TETZMM_SNAPSHOT=<png>` (with `TETZMM_SELECT=<name>`) renders the window to a file and closes it.

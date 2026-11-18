# Tetz's Mod Manager

A personal mod manager for Railroader 0.6 (2026.1): a Windows window (WPF, .NET 10) that installs mods into the game's
home folder, keeps Preferences.json in step, checks Nexus for updates and keeps every installed archive.

## What it does

- **The list comes from the mods themselves.** Each folder in `Mods` is read from its `Info.json` (identifier, name,
  version, author, dependencies), or an old Unity Mod Manager `info.json` (`Id`, `DisplayName`). A mod is known by its
  identifier, never by its folder name or the name of the archive it came in, so the same mod under two names is one
  mod. Nexus details stay behind it, in `%APPDATA%\TetzModManager\state.json`.
- **Packages.** The list shows packages: what one archive (or unzipped folder) installed. One archive can hold several
  mods, at any depth (the game reads only Mods' top folders, so each becomes its own folder there):
  - SCOLD is one row with its three folders; Freeman's GP60 pack one row with three; LIRR GT-1's two nested mods one.
    The details pane lists each mod, with its own enable box.
  - A package is named after the mod whose name best matches the archive's ("SD38-2 and GP38Scripts" -> SD38-2).
  - **Shared mods**: BMAN bundles his GP38Scripts (and the Blomberg trucks) with each pack. Such a mod belongs to every
    package carrying it; installing a pack never puts an older copy over a newer one; uninstalling a pack keeps it
    while another installed package carries it.
  - A new archive updates the installed package with the same **core** (the mods no other mod in the archive
    requires): bundled parts don't make two packs one (BMAN's GP38 and GP40 packs share two of their three mods), and
    packages from different Nexus pages never match.
  - **Add-ons**: an archive without an Info.json whose top folder is an installed mod's folder (Freeman's RTB
    placards for RexsToolBox) puts its files into that folder. They are put back whenever that mod is reinstalled or
    updated, and removed with the add-on.
- **Installs** from an archive (zip; 7z/rar through 7-Zip) or an unzipped folder (both can be dropped on the window),
  or a Nexus link:
  - an installed mod is replaced in its own folder; a new one gets the archive's folder name;
  - plain copies, no hardlinks; each replaced folder is kept for **Roll back** (one earlier version per mod, in
    `%LOCALAPPDATA%\TetzModManager\previous`);
  - Preferences.json gets the new version, or a new entry (enabled, scope 0; client mods in `clientMods`). Everything
    else in the file is written back as it was (two-space indent, its own line endings); the last 20 versions of the
    file are kept in `%APPDATA%\TetzModManager\preferences-backups`;
  - nothing is changed while Railroader runs; it asks first for an older version, a Vortex folder, a blocked folder or
    an add-on.
- **Archive**: each package's archive is copied to the archive folder (default `D:\Railroader Mod Archive`; a folder
  install is zipped); the package's previous archive is deleted, so there is one per package.
- **Import from Vortex** groups the installed mods into packages from Vortex's downloads (each mod belongs to the
  newest download holding it; Vortex names them `<name> <mod id> <version> <timestamp> <id>.zip`), links them to Nexus,
  and can copy those downloads into the archive. **Check for updates** matches each package to its Nexus file (by the
  Nexus version known from the download, else by the Info.json version; `1.0` = `1.0.0`) and follows Nexus's
  file-update chain to the newest file. **Update** downloads it with the API key (Premium: no nxm key needed).
- **nxm links** (Settings > "Handle Nexus links here"): Windows sends `nxm://` links here; links for other games go to
  the handler that was there before (Vortex), and "Give Nexus links back" restores it.
- **Notes**: the same mod in two folders, not in Preferences.json while nothing enabled needs it (libraries required
  by an enabled mod load without an entry), Preferences.json version differing from the folder's, a missing or disabled
  required mod, an enabled incompatible mod, a game-version pin, a package member missing from Mods.

## Build, test, install

```bash
dotnet build TetzModManager.sln
# smoke test: reads the real home folder, installs into a throwaway one
TETZMM_DATA=<scratch> dotnet run --project tools/SmokeTest -- "<home>" "<Vortex downloads>" "<LT zip>" "<older LT zip>" [key file]
# install to %LOCALAPPDATA%\Programs\TetzModManager (close it first; publish fails on a folder name with an apostrophe)
dotnet publish src/App/App.csproj -c Release -o "$LOCALAPPDATA/Programs/TetzModManager"
```

`TETZMM_DATA` moves all of the tool's own files (settings, state, rollback copies, work folder) for tests;
`TETZMM_SNAPSHOT=<png>` (with `TETZMM_SELECT=<name>`) renders the window to a file and closes it.

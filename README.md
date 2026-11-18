# Tetz's Mod Manager

A mod manager for Railroader 0.6 on Windows. It installs mods into the game's home folder, keeps Preferences.json in
step, checks Nexus Mods for updates, handles Nexus "Mod Manager Download" links, and keeps a copy of every installed
archive.

It's a personal tool shared as is: it works for my game, it may not cover yours. Back up your Mods folder and
Preferences.json before trying it.

## Requirements

- Windows 10/11 and the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
- For updates and Nexus downloads: your personal Nexus Mods API key, saved in a text file
  (nexusmods.com > Site preferences > API keys). Downloads through the API need a Premium account; free accounts can
  still use the "Mod Manager Download" button on a mod's page.
- For 7z and rar archives: [7-Zip](https://www.7-zip.org/) in `C:\Program Files\7-Zip`.

## Install

Unzip the release anywhere and run `TetzModManager.exe`. Open **Settings...** to choose the archive folder and the API
key file, and to make Nexus links open here ("Handle Nexus links here"; links for other games are passed on to the
handler that was there before, and "Give Nexus links back" restores it).

## What it does

- **The list comes from the mods themselves.** Each folder in `Mods` is read from its `Info.json` (identifier, name,
  version, author, dependencies). A mod is known by its identifier, never by its folder name or the name of the archive
  it came in, so the same mod under two names is one mod.
- **The game's home folder** is found the way the game finds it: Steam's `-homedir` launch option when set, else
  `Documents\Railroader`. A folder set in Settings overrides it.
- **Packages.** The list shows packages: what one archive (or unzipped folder) installed. One archive can hold several
  mods, at any depth (the game reads only Mods' top folders, so each becomes its own folder there). The details pane
  lists each mod, with its own enable box.
  - **Shared mods**: some authors bundle a library with each of their packs. Such a mod belongs to every package
    carrying it; installing a pack never puts an older copy over a newer one; uninstalling a pack keeps it while another
    installed package carries it.
  - A new archive updates the installed package with the same **core** (the mods no other mod in the archive requires),
    so bundled parts don't merge two packs, and packages from different Nexus pages never match.
  - **Add-ons**: an archive without an `Info.json` whose top folder is an installed mod's folder (extra files for
    another mod) puts its files into that folder. They are put back whenever that mod is reinstalled or updated, and
    removed with the add-on.
  - **Install as separate mods...** installs an archive that is only a bundle of unrelated mods (a test pack) as one
    package per mod.
- **Installs** from an archive (zip; 7z/rar through 7-Zip), an unzipped folder (both can be dropped on the window), or a
  Nexus link:
  - an installed mod is replaced in its own folder; a new one gets the archive's folder name;
  - a mod with the same version but newer files (test builds often keep their version) counts as an update;
  - each replaced folder is kept for **Roll back** (one earlier version per mod);
  - Preferences.json gets the new version, or a new enabled entry. Everything else in the file is written back as it
    was; the last 20 versions of the file are kept in `%APPDATA%\TetzModManager\preferences-backups`;
  - nothing is changed while Railroader runs; it asks first for an older version, a blocked folder or an add-on.
- **Archive**: each package's archive is copied to the archive folder (a folder install is zipped); the package's
  previous archive is deleted, so there is one per package.
- **Check for updates** matches each package to its Nexus file and follows Nexus's file-update chain to the newest file.
  **Update** downloads and installs it.
- **Import from Vortex** groups the installed mods into packages from Vortex's Railroader downloads and links them to
  Nexus (Vortex names downloads `<name> <mod id> <version> <timestamp> <id>.zip`); it can copy those downloads into the
  archive. Before leaving Vortex, note that removing a game from Vortex deletes every file it deployed: copy the
  deployed mods out first.
- **Right-click menus**: on a package, Update, Enable/Disable (several packages at once), Open folder, Show archive
  in Explorer, Nexus page, Link to Nexus, Copy name/identifiers, Roll back and Uninstall; on a mod in the details
  pane, Enable/Disable, Open folder and Copy identifier.
- **Windows remember** their size and position (and the main window its splitters) between runs.
- **Notes** flag the same mod in two folders, a mod not in Preferences.json while nothing enabled needs it, a
  Preferences.json version differing from the folder's, a missing or disabled required mod, an enabled incompatible mod,
  a game-version pin, and a package member missing from Mods.

Its own files: `%APPDATA%\TetzModManager` (settings, package list, Preferences.json backups, window places) and
`%LOCALAPPDATA%\TetzModManager` (rollback copies, work folder).

## Build

```bash
dotnet build TetzModManager.sln
dotnet publish src/App/App.csproj -c Release -o <folder>   # the folder name must not contain an apostrophe
# release build: no debug files or local paths in the DLLs
dotnet publish src/App/App.csproj -c Release -o <folder> -p:DebugType=none -p:DebugSymbols=false "-p:PathMap=<repo folder>=."
# smoke test: reads a real home folder, installs into a throwaway one
TETZMM_DATA=<scratch> dotnet run --project tools/SmokeTest -- "<home>" "<folder of mod zips: Vortex downloads or the archive>" "<mod zip>" "<older zip of that mod>" [key file]
```

`TETZMM_DATA` moves all of the tool's own files (settings, state, rollback copies, work folder) for tests;
`TETZMM_SNAPSHOT=<png>` (with `TETZMM_SELECT=<name>`) renders the window to a file and closes it.

## Licence

MIT, see [LICENSE](LICENSE).

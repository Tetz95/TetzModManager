using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TetzModManager.App;

/// <summary>A row of the list: a package, named after its main mod, with its Nexus details behind it.</summary>
public sealed class PackageRow(InstalledPackage p)
{
	public InstalledPackage Package { get; } = p;
	public string Name => Package.Name;
	public string Version => Package.Record?.IsAddOn == true ? Package.Record.InstalledNexusVersion ?? ""
		: !Versions.Same(Package.InstalledVersion, Package.Version) ? $"{Package.Version} ({Package.InstalledVersion})" : Package.Version;
	public string Latest => Package.Record?.LatestVersion is string v ? (Package.Record.NewerThanNexus ? v + " (installed is newer)" : v) : "";
	public string Author => Package.Author;
	public string Contents => Package.Record?.IsAddOn == true ? "add-on for " + string.Join(", ", Package.Record.Overlays.Keys)
		: Package.Members.Count == 1 ? Package.Members[0].Manifest.Identifier ?? Package.Members[0].Key
		: $"{Package.Members.Count} mods" + (Package.Shared.Count > 0 ? $" ({Package.Shared.Count} shared)" : "");
	public string NexusId => Package.Record?.NexusModId?.ToString() ?? "";

	private List<InstalledMod> Toggleable => Package.Own.Where(m => m.InPreferences).ToList();
	public bool? Enabled
	{
		get
		{
			List<InstalledMod> t = Toggleable;
			if (t.Count == 0)
			{
				return Package.Members.Count > 0 && Package.Members.All(m => m.LoadedAsDependency || m.Enabled == true) ? true : Package.Record?.IsAddOn == true ? true : null;
			}
			return t.All(m => m.Enabled == true) ? true : t.All(m => m.Enabled == false) ? false : null;
		}
	}
	public int EnabledSort => Enabled switch { true => 0, null => 1, false => 2 };
	public bool CanToggle => Toggleable.Count > 0;
	public string EnabledTip => CanToggle ? "Enables or disables the package's mods in Preferences.json" : "Loaded because an enabled mod requires it";
	public bool IsDisabled => Enabled == false;
	public bool UpdateAvailable => Package.UpdateAvailable;
	public bool HasWarning => Package.Notes.Count > 0;
	public string NotesText => string.Join("; ", Package.Notes);
}

/// <summary>A mod inside the selected package.</summary>
public sealed class MemberRow(InstalledMod m, InstalledPackage p, IEnumerable<string> sharedWith)
{
	public InstalledMod Mod { get; } = m;
	public string Name => Mod.Manifest.Name;
	public string Version => Mod.Manifest.Version;
	public string Key => Mod.Manifest.Identifier ?? Mod.Key;
	public string Folder => Mod.FolderName;
	public bool? Enabled => Mod.LoadedAsDependency ? true : Mod.Enabled;
	public bool CanToggle => Mod.InPreferences;
	public string EnabledTip => Mod.LoadedAsDependency ? "Loaded because an enabled mod requires it" : Mod.InPreferences ? "Enabled in Preferences.json" : "";
	public bool IsDisabled => Mod.Enabled == false;
	public bool UpdateAvailable => false;
	public bool HasWarning => Mod.Notes.Count > 0;
	public string NotesText => string.Join("; ", (p.Shared.Contains(Mod.Key) ? [$"also in {string.Join(", ", sharedWith)}"] : Array.Empty<string>())
		.Concat(Mod.VortexManaged ? ["deployed by Vortex"] : Array.Empty<string>()).Concat(Mod.Notes));
}

public partial class MainWindow : Window
{
	private readonly Settings _settings = Settings.Load();
	private StateStore _state = StateStore.Load();
	private ScanResult _scan = new([], []);
	private List<PackageRow> _rows = [];
	private readonly DispatcherTimer _gameTimer = new() { Interval = TimeSpan.FromSeconds(3) };
	private bool _busy;

	public MainWindow()
	{
		InitializeComponent();
		RememberPlace();
		_gameTimer.Tick += (_, _) => ShowGameStatus();
		_gameTimer.Start();
	}

	/// <summary>The window's size, position and splitters as they were when it last closed.</summary>
	private void RememberPlace()
	{
		if (WindowPlaces.Get("main") is { } p)
		{
			if (p.Extra.TryGetValue("detailsHeight", out double h) && h >= 120)
			{
				DetailsRow.Height = new GridLength(h);
			}
			if (p.Extra.TryGetValue("detailsShare", out double share) && share is > 0.1 and < 0.9)
			{
				DetailsColumn.Width = new GridLength(share, GridUnitType.Star);
				LogColumn.Width = new GridLength(1 - share, GridUnitType.Star);
			}
		}
		WindowPlaces.Track(this, "main", saveExtra: place =>
		{
			place.Extra["detailsHeight"] = DetailsRow.ActualHeight;
			double total = DetailsColumn.ActualWidth + LogColumn.ActualWidth;
			if (total > 0)
			{
				place.Extra["detailsShare"] = DetailsColumn.ActualWidth / total;
			}
		});
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		if (!File.Exists(Path.Combine(Settings.DataDir, "settings.json")))
		{
			_settings.Save();
		}
		Refresh();
		ShowGameStatus();
		Log($"Mods: {_settings.ModsDir}" + (string.IsNullOrWhiteSpace(_settings.HomeDir) ? $" ({Steam.DetectHome().From})" : " (set in Settings)"));
		if (Steam.GameDir() is string game)
		{
			Log($"Game: {game}");
		}
		Log($"Archive: {_settings.ArchiveDir}");
		if (_state.Packages.Count == 0)
		{
			Log("No packages yet: \"Import from Vortex\" groups the installed mods and links them to Nexus.");
		}
		if (NxmHandler.IsOurs())
		{
			Log("Nexus \"Mod Manager Download\" links come here.");
		}
		Snapshot();
	}

	/// <summary>Tests: TETZMM_SNAPSHOT=&lt;png&gt; renders the window to a file and closes (TETZMM_SELECT picks a row first).</summary>
	private void Snapshot()
	{
		if (Environment.GetEnvironmentVariable("TETZMM_SNAPSHOT") is not string shot)
		{
			return;
		}
		if (Environment.GetEnvironmentVariable("TETZMM_SELECT") is string sel)
		{
			Grid.SelectedItem = Grid.Items.Cast<PackageRow>().FirstOrDefault(r => r.Name.Contains(sel, StringComparison.OrdinalIgnoreCase));
			Grid.ScrollIntoView(Grid.SelectedItem);
		}
		Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
		{
			System.Windows.Media.Imaging.RenderTargetBitmap bmp = new((int)ActualWidth, (int)ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
			bmp.Render((System.Windows.Media.Visual)Content);
			System.Windows.Media.Imaging.PngBitmapEncoder enc = new();
			enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
			using (FileStream fs = File.Create(shot))
			{
				enc.Save(fs);
			}
			Close();
		});
	}

	// ---- list ----

	private void Refresh()
	{
		Preferences? prefs = null;
		try
		{
			prefs = Preferences.Load(_settings.PreferencesPath);
		}
		catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException)
		{
			Log("Preferences.json can't be read: " + e.Message);
		}
		string? selected = (Grid.SelectedItem as PackageRow)?.Package.Id;
		_scan = Scanner.Scan(_settings, prefs, _state);
		_rows = _scan.Packages.Where(p => p.Members.Count > 0 || p.Record?.IsAddOn == true || p.Missing.Count > 0).Select(p => new PackageRow(p)).ToList();
		ApplyFilter();
		if (selected != null)
		{
			Grid.SelectedItem = Grid.Items.Cast<PackageRow>().FirstOrDefault(r => r.Package.Id == selected);
		}
		int updates = _rows.Count(r => r.UpdateAvailable);
		Counts.Text = $"{_rows.Count} packages, {_scan.Mods.Count} mod folders, {_scan.Mods.Count(m => m.Enabled == true)} enabled, {updates} updates, {_rows.Count(r => r.HasWarning)} with notes";
		UpdateAllButton.IsEnabled = updates > 0;
		UpdateAllButton.Content = updates > 0 ? $"Update all ({updates})" : "Update all";
		ShowDetails();
	}

	private void ApplyFilter()
	{
		if (Grid == null)
		{
			return;
		}
		string f = FilterBox?.Text?.Trim() ?? "";
		int view = ViewBox?.SelectedIndex ?? 0;
		IEnumerable<PackageRow> rows = _rows;
		if (f.Length > 0)
		{
			rows = rows.Where(r => r.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Author.Contains(f, StringComparison.OrdinalIgnoreCase)
				|| r.Package.Members.Any(m => m.Manifest.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || m.Key.Contains(f, StringComparison.OrdinalIgnoreCase)
					|| m.FolderName.Contains(f, StringComparison.OrdinalIgnoreCase)));
		}
		rows = view switch
		{
			1 => rows.Where(r => r.UpdateAvailable),
			2 => rows.Where(r => r.HasWarning),
			3 => rows.Where(r => r.Package.Notes.Any(n => n.Contains("Duplicate"))),
			4 => rows.Where(r => r.IsDisabled || r.Package.Members.Any(m => m.Enabled == false)),
			5 => rows.Where(r => r.Package.Members.Count > 1),
			6 => rows.Where(r => r.Package.Record?.IsAddOn == true),
			7 => rows.Where(r => r.Package.Record?.NexusModId == null),
			8 => rows.Where(r => r.Package.Members.Any(m => m.VortexManaged)),
			_ => rows,
		};
		Grid.ItemsSource = rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private void OnFilterChanged(object sender, EventArgs e) => ApplyFilter();

	private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetails();

	private List<PackageRow> Selected => Grid.SelectedItems.Cast<PackageRow>().ToList();

	private void ShowDetails()
	{
		List<PackageRow> sel = Selected;
		PackageRow? row = sel.Count == 1 ? sel[0] : null;
		UpdateButton.IsEnabled = sel.Any(r => r.UpdateAvailable);
		NexusButton.IsEnabled = row?.Package.Record?.NexusModId != null;
		LinkButton.IsEnabled = row != null;
		FolderButton.IsEnabled = row?.Package.Members.Count > 0 || row?.Package.Record?.IsAddOn == true;
		RollBackButton.IsEnabled = row != null && row.Package.Members.Any(m => Installer.HasPrevious(m.Key));
		UninstallButton.IsEnabled = row != null;
		if (row == null)
		{
			Details.Text = sel.Count > 1 ? $"{sel.Count} packages selected" : "";
			MembersGrid.ItemsSource = null;
			return;
		}
		InstalledPackage p = row.Package;
		PackageRecord? r = p.Record;
		List<string> lines = [$"{p.Name} {p.Version}" + (p.Author.Length > 0 ? $" by {p.Author}" : "")];
		if (p.Primary?.Manifest.Description is { Length: > 0 } desc)
		{
			lines.Add(desc);
		}
		if (r == null)
		{
			lines.Add("Not part of a package yet (installed before this tool, and not found in Vortex's downloads).");
		}
		else
		{
			lines.Add($"Installed from: {r.SourceName ?? r.Source}" + (r.InstalledAt != null ? $" ({r.InstalledAt:g})" : ""));
			if (r.NexusModId != null)
			{
				lines.Add($"Nexus: mod {r.NexusModId}" + (r.NexusFileId != null ? $", file {r.NexusFileId}" : "") + (r.InstalledNexusVersion != null ? $", version {r.InstalledNexusVersion}" : "")
					+ (r.LatestVersion != null ? $"; newest {r.LatestVersion}" + (r.CheckedAt != null ? $" (checked {r.CheckedAt:g})" : "") : "") + $" [linked by {r.LinkSource}]");
			}
			lines.Add(r.Archive != null ? $"Archive: {r.Archive}" + (File.Exists(r.Archive) ? "" : " (missing)") : "Archive: none yet");
			foreach ((string key, List<string> files) in r.Overlays)
			{
				lines.Add($"Adds {files.Count} files to {key}");
			}
		}
		lines.AddRange(p.Missing.Select(k => $"• {k} is part of this package but not in Mods"));
		Details.Text = string.Join(Environment.NewLine, lines);
		MembersGrid.ItemsSource = p.Members.Select(m => new MemberRow(m, p,
			_scan.Packages.Where(o => o != p && o.Members.Contains(m)).Select(o => o.Name))).ToList();
	}

	private void ShowGameStatus()
	{
		bool running = Game.IsRunning(_settings);
		GameStatus.Text = running ? "Railroader is running: changes wait until it's closed" : "Railroader is closed";
		GameStatus.Foreground = running ? System.Windows.Media.Brushes.DarkRed : System.Windows.Media.Brushes.DarkGreen;
	}

	private void Log(string line)
	{
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.Invoke(() => Log(line));
			return;
		}
		LogBox.AppendText($"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
		LogBox.ScrollToEnd();
		// Also to %APPDATA%\TetzModManager\log.txt (the previous run's in log.old.txt), for looking back after closing.
		try
		{
			if (!_logStarted)
			{
				_logStarted = true;
				Directory.CreateDirectory(Settings.DataDir);
				if (File.Exists(LogPath))
				{
					File.Move(LogPath, Path.Combine(Settings.DataDir, "log.old.txt"), true);
				}
			}
			File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
		}
	}

	private bool _logStarted;
	private static string LogPath => Path.Combine(Settings.DataDir, "log.txt");

	// ---- running work off the UI thread ----

	// Jobs asked for while another runs (an nxm link or archive handed over during the update check): run in turn.
	private readonly Queue<(string What, Func<Task> Work, bool NeedsGameClosed, TaskCompletionSource Done)> _queue = new();

	/// <summary>
	/// Runs one job at a time. A job asked for while another runs waits its turn; the returned task finishes when the
	/// job has run (or was skipped), so callers' resources (a Nexus client) stay alive until then.
	/// </summary>
	private async Task Run(string what, Func<Task> work, bool needsGameClosed = true)
	{
		if (_busy)
		{
			TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
			_queue.Enqueue((what, work, needsGameClosed, done));
			Log($"Queued: {what} starts once the current job has finished.");
			await done.Task;
			return;
		}
		_busy = true;
		Progress.Visibility = Visibility.Visible;
		Progress.IsIndeterminate = true;
		try
		{
			await Execute(what, work);
			while (_queue.TryDequeue(out var next))
			{
				// The game may have been started while the job waited.
				if (next.NeedsGameClosed && Game.IsRunning(_settings))
				{
					Log($"Queued {next.What} not started: Railroader is running. Try again once the game is closed.");
				}
				else
				{
					Log($"Starting queued {next.What}.");
					await Execute(next.What, next.Work);
				}
				next.Done.SetResult();
			}
		}
		finally
		{
			_busy = false;
			Progress.Visibility = Visibility.Collapsed;
		}
	}

	private async Task Execute(string what, Func<Task> work)
	{
		try
		{
			await work();
		}
		catch (Exception e)
		{
			Log($"{what} failed: {e.Message}");
			MessageBox.Show(this, e.Message, what, MessageBoxButton.OK, MessageBoxImage.Warning);
		}
		finally
		{
			_state = StateStore.Load();
			Refresh();
		}
	}

	private bool GameClosedOrSay()
	{
		if (!Game.IsRunning(_settings))
		{
			return true;
		}
		MessageBox.Show(this, "Close Railroader first: mods and Preferences.json aren't changed while it runs.", "Railroader is running", MessageBoxButton.OK, MessageBoxImage.Information);
		return false;
	}

	private NexusClient? Nexus()
	{
		string? key = _settings.ReadNexusKey();
		if (key == null)
		{
			MessageBox.Show(this, "Set the file with your Nexus API key in Settings first.", "Nexus", MessageBoxButton.OK, MessageBoxImage.Information);
			return null;
		}
		return new NexusClient(key);
	}

	// ---- installs ----

	/// <summary>
	/// Installs an archive or folder. Asks first only when something needs a look: an older version, a Vortex folder, a
	/// blocked folder, or an add-on.
	/// </summary>
	private async Task Install(string path, NexusSource? nexus, bool separate = false)
	{
		ScanResult installed = Scanner.Scan(_settings, null, _state);
		InstallPlan plan = await Task.Run(() => Installer.Plan(_settings, path, installed, _state, nexus?.ModId, nexus?.DisplayName));
		if (separate)
		{
			try
			{
				await InstallSeparately(path, plan, installed);
			}
			finally
			{
				plan.Dispose();
			}
			return;
		}
		try
		{
			List<string> lines = [];
			bool ask = plan.Overlays.Count > 0;
			if (plan.Mods.Count > 0 && plan.Mods.All(m => m.Manifest.Umm))
			{
				// Legacy (Unity Mod Manager) mods, e.g. an author's Legacy update on a page linked to a 0.6 conversion. The
				// game loads none of them; data-only ones (no assembly) work only where a 0.6 mod reads them.
				lines.Add(plan.Mods.Any(m => !string.IsNullOrEmpty(m.Manifest.UmmAssembly))
					? "Only Legacy (Unity Mod Manager) mods, with DLLs: Railroader 0.6 doesn't load them."
					: "Only Legacy (Unity Mod Manager) mods without DLLs: Railroader 0.6 doesn't load them itself; they work only if "
						+ "another mod reads them (Rex's Tool Box reads Reaper's sound packs, for example).");
				ask = true;
			}
			foreach (PlannedMod p in plan.Mods)
			{
				lines.Add(Describe(p));
				ask |= p.Action is PlanAction.Downgrade or PlanAction.Blocked;
				if (p.ReplacesVortexFolder)
				{
					lines.Add("    deployed by Vortex now: Vortex will report it as changed until Railroader is removed from Vortex");
					ask = true;
				}
			}
			foreach (PlannedOverlay o in plan.Overlays)
			{
				lines.Add($"• {o.Files.Count} add-on files into {o.Target.Manifest.Name} (\"{o.Target.FolderName}\"), put back whenever that mod is reinstalled");
			}
			string header = plan.Package != null ? $"Updates the installed package \"{_scan.Packages.FirstOrDefault(p => p.Record == plan.Package)?.Name ?? plan.Name}\"" : $"New package \"{plan.Name}\"";
			if (ask && MessageBox.Show(this, $"{Path.GetFileName(path.TrimEnd('\\'))}\n{header}:\n\n{string.Join("\n", lines)}\n\nGo ahead?",
				"Install", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
			{
				Log($"Not installed: {Path.GetFileName(path.TrimEnd('\\'))}");
				return;
			}
			Log($"{Path.GetFileName(path.TrimEnd('\\'))}: {header}");
			await Task.Run(() => Installer.Apply(_settings, _state, plan, nexus, Log));
		}
		finally
		{
			plan.Dispose();
		}
	}

	private static string Describe(PlannedMod p)
	{
		string folder = Path.GetFileName(p.Target);
		string version = p.Existing != null && p.Existing.Manifest.Version != p.Manifest.Version ? $"{p.Existing.Manifest.Version} → {p.Manifest.Version}" : p.Manifest.Version;
		string what = p.Action switch
		{
			PlanAction.Install => $"new, folder \"{folder}\"",
			PlanAction.Update => p.Reason ?? "update",
			PlanAction.Reinstall => "same version, copied again",
			PlanAction.Downgrade => "OLDER than the installed one",
			PlanAction.KeepInstalled => $"{(p.Shared ? "shared, " : "")}left as it is: {p.Reason}",
			_ => $"skipped: {p.Reason}",
		};
		return $"{(p.Copies ? "•" : "–")} {p.Manifest.Name} {version} ({what})";
	}

	/// <summary>Each mod of the archive as its own package; always asks first, listing them all.</summary>
	private async Task InstallSeparately(string path, InstallPlan plan, ScanResult installed)
	{
		List<InstallPlan> plans = Installer.Split(_settings, plan, installed, _state, out List<PlannedMod> skipped);
		try
		{
			List<string> lines = plans.Select(s => Describe(s.Mods[0]) + (s.Package != null ? "  [its own package]" : "  [new package]")).ToList();
			lines.AddRange(skipped.Select(Describe));
			string name = Path.GetFileName(path.TrimEnd('\\'));
			if (plans.Count == 0)
			{
				Log($"{name}: nothing to install");
				lines.ForEach(l => Log("  " + l));
				return;
			}
			if (MessageBox.Show(this, $"{name}\nEach mod as its own package:\n\n{string.Join("\n", lines)}\n\nGo ahead?",
				"Install as separate mods", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
			{
				Log($"Not installed: {name}");
				return;
			}
			Log($"{name}: {plans.Count} mods as separate packages");
			foreach (InstallPlan s in plans)
			{
				await Task.Run(() => Installer.Apply(_settings, _state, s, null, Log));
			}
		}
		finally
		{
			plans.ForEach(s => s.Dispose());
		}
	}

	/// <summary>The Nexus page's name, which names a package of several mods; null when Nexus doesn't answer.</summary>
	private async Task<string?> PageName(NexusClient nexus, int modId)
	{
		try
		{
			string name = (await nexus.Mod(modId)).Name.Trim();
			return name.Length > 0 ? name : null;
		}
		catch (Exception e)
		{
			Log($"Nexus page name for {modId}: {e.Message}");
			return null;
		}
	}

	private async Task DownloadAndInstall(NexusClient nexus, int modId, int fileId, string? key, string? expires)
	{
		NexusFile file = await nexus.File(modId, fileId);
		Log($"Downloading {file.Name} {file.Version} ({file.FileName})...");
		string url = await nexus.DownloadLink(modId, fileId, key, expires);
		string dest = Path.Combine(Settings.WorkDir, "downloads", file.FileName);
		Progress.IsIndeterminate = false;
		await nexus.Download(url, dest, new Progress<double>(p => Progress.Value = p * 100));
		Progress.IsIndeterminate = true;
		try
		{
			await Install(dest, new NexusSource(modId, fileId, file.FileName, file.Version, await PageName(nexus, modId) ?? Installer.CleanName(file.Name)));
		}
		finally
		{
			File.Delete(dest);
		}
	}

	/// <summary>A start argument or a forwarded one: an nxm link, an archive or a folder.</summary>
	public async void HandleArgument(string arg)
	{
		if (arg.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
		{
			await HandleNxm(arg);
		}
		else if ((File.Exists(arg) || Directory.Exists(arg)) && GameClosedOrSay())
		{
			await Run("Install", () => Install(arg, null));
		}
	}

	private async Task HandleNxm(string url)
	{
		NxmLink? link = NxmLink.Parse(url);
		if (link == null)
		{
			Log("Not a Nexus mod link: " + url);
			return;
		}
		if (link.Game != NexusClient.GameDomain)
		{
			try
			{
				NxmHandler.Forward(_settings, url);
				Log($"Passed a {link.Game} link on to the previous handler.");
			}
			catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
			{
				Log(e.Message);
			}
			return;
		}
		if (!GameClosedOrSay())
		{
			Log($"Nexus link for mod {link.ModId} file {link.FileId} not installed: Railroader is running. Click it again once the game is closed.");
			return;
		}
		using NexusClient? nexus = Nexus();
		if (nexus == null)
		{
			return;
		}
		await Run("Nexus download", () => DownloadAndInstall(nexus, link.ModId, link.FileId, link.Key, link.Expires));
	}

	private async Task InstallAll(IEnumerable<string> paths, bool separate = false)
	{
		if (!GameClosedOrSay())
		{
			return;
		}
		await Run("Install", async () =>
		{
			foreach (string p in paths)
			{
				await Install(p, null, separate);
			}
		});
	}

	/// <summary>A bundle of separate mods (Freeman's test zips): each mod becomes its own package.</summary>
	private async void OnInstallSeparately(object sender, RoutedEventArgs e)
	{
		OpenFileDialog dlg = new()
		{
			Filter = "Mod archives|*.zip;*.7z;*.rar|All files|*.*",
			Title = "An archive holding separate mods: each is installed as its own package",
		};
		if (dlg.ShowDialog(this) == true)
		{
			await InstallAll(dlg.FileNames, separate: true);
		}
	}

	private async void OnInstallArchive(object sender, RoutedEventArgs e)
	{
		OpenFileDialog dlg = new() { Filter = "Mod archives|*.zip;*.7z;*.rar|All files|*.*", Multiselect = true };
		if (dlg.ShowDialog(this) == true)
		{
			await InstallAll(dlg.FileNames);
		}
	}

	private async void OnInstallFolder(object sender, RoutedEventArgs e)
	{
		OpenFolderDialog dlg = new() { Multiselect = true, Title = "An unzipped download: a folder holding one or more mod folders" };
		if (dlg.ShowDialog(this) == true)
		{
			await InstallAll(dlg.FolderNames);
		}
	}

	private void OnDragOver(object sender, DragEventArgs e)
	{
		e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
		e.Handled = true;
	}

	private async void OnDrop(object sender, DragEventArgs e)
	{
		if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
		{
			await InstallAll(paths.Where(p => File.Exists(p) || Directory.Exists(p)));
		}
	}

	// ---- updates ----

	private async void OnCheckUpdates(object sender, RoutedEventArgs e)
	{
		using NexusClient? nexus = Nexus();
		if (nexus == null)
		{
			return;
		}
		await Run("Update check", async () =>
		{
			Log($"Checking {_scan.Packages.Count(p => p.Record?.NexusModId != null)} linked packages on Nexus...");
			int n = await Updates.Check(nexus, _state, _scan.Packages, Log);
			Log(n == 0 ? "Everything is up to date." : $"{n} updates.");
		}, needsGameClosed: false);
	}

	private async Task UpdateRows(List<PackageRow> rows)
	{
		rows = rows.Where(r => r.UpdateAvailable && r.Package.Record?.LatestFileId != null).ToList();
		if (rows.Count == 0 || !GameClosedOrSay())
		{
			return;
		}
		using NexusClient? nexus = Nexus();
		if (nexus == null)
		{
			return;
		}
		await Run("Update", async () =>
		{
			// Packages updated by the same Nexus file (one pack installed as separate packages): download it once.
			foreach ((int mod, int file) in rows.Select(r => (r.Package.Record!.NexusModId!.Value, r.Package.Record.LatestFileId!.Value)).Distinct())
			{
				await DownloadAndInstall(nexus, mod, file, null, null);
			}
		});
	}

	private async void OnUpdateSelected(object sender, RoutedEventArgs e) => await UpdateRows(Selected);

	private async void OnUpdateAll(object sender, RoutedEventArgs e)
	{
		List<PackageRow> rows = _rows.Where(r => r.UpdateAvailable).ToList();
		if (MessageBox.Show(this, "Update these from Nexus?\n\n" + string.Join("\n", rows.Select(r => $"{r.Name}  {r.Package.InstalledVersion} → {r.Latest}")),
			"Update all", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
		{
			await UpdateRows(rows);
		}
	}

	private async void OnVortexImport(object sender, RoutedEventArgs e)
	{
		MessageBoxResult copy = MessageBox.Show(this,
			"Group the installed mods into packages and link them to Nexus, from Vortex's downloads.\n\n"
			+ $"Also copy each package's download (where it matches the installed version) into the archive folder?\n{_settings.ArchiveDir}",
			"Import from Vortex", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
		if (copy == MessageBoxResult.Cancel)
		{
			return;
		}
		await Run("Import from Vortex", () => Task.Run(() =>
		{
			(int made, int archived) = VortexImport.Run(_settings, _state, Scanner.Scan(_settings, Preferences.Load(_settings.PreferencesPath), _state), copy == MessageBoxResult.Yes, Log);
			Log($"{made} new packages" + (copy == MessageBoxResult.Yes ? $", {archived} archives copied" : "") + ". \"Check for updates\" matches each to its Nexus file.");
		}));
	}

	// ---- enabling ----

	private void SetEnabled(IEnumerable<InstalledMod> mods, bool on, string what)
	{
		if (!GameClosedOrSay())
		{
			Refresh();
			return;
		}
		try
		{
			Preferences prefs = Preferences.Load(_settings.PreferencesPath);
			foreach (InstalledMod m in mods.Where(m => m.InPreferences && m.Manifest.Identifier != null))
			{
				prefs.SetEnabled(m.Manifest.Identifier!, on);
			}
			prefs.Save();
			Log($"{(on ? "Enabled" : "Disabled")} {what}");
		}
		catch (Exception ex) when (ex is IOException or InvalidOperationException)
		{
			Log(ex.Message);
		}
		Refresh();
	}

	private void OnTogglePackage(object sender, RoutedEventArgs e)
	{
		if (sender is CheckBox { DataContext: PackageRow row })
		{
			SetEnabled(row.Package.Own, row.Enabled != true, row.Name);
		}
	}

	private void OnToggleMember(object sender, RoutedEventArgs e)
	{
		if (sender is CheckBox { DataContext: MemberRow row })
		{
			SetEnabled([row.Mod], row.Mod.Enabled != true, row.Name);
		}
	}

	// ---- per package ----

	private void OnNexusPage(object sender, RoutedEventArgs e)
	{
		if (Selected.FirstOrDefault()?.Package.Record?.NexusModId is int id)
		{
			Process.Start(new ProcessStartInfo($"https://www.nexusmods.com/{NexusClient.GameDomain}/mods/{id}") { UseShellExecute = true });
		}
	}

	private void OnLink(object sender, RoutedEventArgs e)
	{
		if (Selected.FirstOrDefault() is not PackageRow row)
		{
			return;
		}
		string current = row.Package.Record?.NexusModId?.ToString() ?? "";
		string? answer = InputDialog.Ask(this, "Link to Nexus", $"Nexus mod id (or page URL) for {row.Name}.\nLeave empty to unlink.", current);
		if (answer == null)
		{
			return;
		}
		PackageRecord r = row.Package.Record ?? NewRecord(row.Package);
		System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(answer, @"(?:mods/)?(\d+)\s*/?\s*$");
		r.NexusFileId = null;
		r.LatestVersion = null;
		r.LatestFileId = null;
		r.InstalledNexusVersion = null;
		if (answer.Trim().Length == 0)
		{
			r.NexusModId = null;
			Log($"Unlinked {row.Name} from Nexus");
		}
		else if (m.Success)
		{
			r.NexusModId = int.Parse(m.Groups[1].Value);
			r.LinkSource = "manual";
			Log($"Linked {row.Name} to Nexus mod {r.NexusModId}");
		}
		_state.Save();
		Refresh();
	}

	/// <summary>A record for a mod no package claims yet (linking it to Nexus makes it a package of one).</summary>
	private PackageRecord NewRecord(InstalledPackage p)
	{
		PackageRecord r = new() { Members = p.Members.Select(m => m.Key).ToList(), Source = "existing", SourceName = p.Members[0].FolderName };
		_state.Packages.Add(r);
		return r;
	}

	private void OnOpenFolder(object sender, RoutedEventArgs e)
	{
		InstalledMod? m = (MembersGrid.SelectedItem as MemberRow)?.Mod ?? Selected.FirstOrDefault()?.Package.Members.FirstOrDefault()
			?? _scan.Mods.FirstOrDefault(x => Selected.FirstOrDefault()?.Package.Record?.Overlays.ContainsKey(x.Key) == true);
		if (m != null)
		{
			Process.Start("explorer.exe", $"\"{m.Folder}\"");
		}
	}

	private async void OnRollBack(object sender, RoutedEventArgs e)
	{
		if (Selected.FirstOrDefault() is not PackageRow row || !GameClosedOrSay())
		{
			return;
		}
		List<string> mods = row.Package.Members.Where(m => Installer.HasPrevious(m.Key)).Select(m => m.Manifest.Name).ToList();
		if (MessageBox.Show(this, $"Put back the copies kept from before the last install of:\n\n{string.Join("\n", mods)}", "Roll back",
			MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
		{
			await Run("Roll back", () => Task.Run(() => Installer.RollBack(_settings, _state, row.Package, Log)));
		}
	}

	private async void OnUninstall(object sender, RoutedEventArgs e)
	{
		if (Selected.FirstOrDefault() is not PackageRow row || !GameClosedOrSay())
		{
			return;
		}
		InstalledPackage p = row.Package;
		List<string> lines = p.Own.Select(m => $"• {m.Manifest.Name} (\"{m.FolderName}\")").ToList();
		lines.AddRange(p.Members.Where(m => p.Shared.Contains(m.Key)).Select(m => $"– {m.Manifest.Name} stays: another package uses it"));
		if (p.Record != null)
		{
			lines.AddRange(p.Record.Overlays.Select(kv => $"• its {kv.Value.Count} files in {kv.Key}"));
		}
		if (MessageBox.Show(this, $"Uninstall {row.Name}?\n\n{string.Join("\n", lines)}\n\nFolders move out of Mods (kept for rollback) and leave Preferences.json. The archive stays.",
			"Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
		{
			await Run("Uninstall", () => Task.Run(() => Installer.Uninstall(_settings, _state, p, Log)));
		}
	}

	// ---- right-click menus ----

	/// <summary>Right-clicking a row that isn't selected selects it alone (the menu then acts on it).</summary>
	private void OnRowRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		DependencyObject? d = e.OriginalSource as DependencyObject;
		while (d != null && d is not DataGridRow)
		{
			d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
		}
		if (d is DataGridRow { IsSelected: false } row && sender is DataGrid grid)
		{
			grid.SelectedItems.Clear();
			row.IsSelected = true;
			row.Focus();
		}
	}

	private void OnPackageMenuOpening(object sender, ContextMenuEventArgs e)
	{
		List<PackageRow> sel = Selected;
		if (sel.Count == 0)
		{
			e.Handled = true;
			return;
		}
		PackageRow? row = sel.Count == 1 ? sel[0] : null;
		MenuUpdate.IsEnabled = UpdateButton.IsEnabled;
		MenuEnable.IsEnabled = sel.Any(r => r.CanToggle && r.Enabled != true);
		MenuDisable.IsEnabled = sel.Any(r => r.CanToggle && r.Enabled != false);
		MenuFolder.IsEnabled = FolderButton.IsEnabled;
		MenuArchive.IsEnabled = row?.Package.Record?.Archive is string a && File.Exists(a);
		MenuNexus.IsEnabled = NexusButton.IsEnabled;
		MenuLink.IsEnabled = LinkButton.IsEnabled;
		MenuCopyIds.IsEnabled = sel.Any(r => r.Package.Members.Count > 0);
		MenuRollBack.IsEnabled = RollBackButton.IsEnabled;
		MenuUninstall.IsEnabled = UninstallButton.IsEnabled;
		string many = sel.Count > 1 ? $" ({sel.Count} packages)" : "";
		MenuEnable.Header = "Enable" + many;
		MenuDisable.Header = "Disable" + many;
		MenuUpdate.Header = sel.Count(r => r.UpdateAvailable) > 1 ? $"Update ({sel.Count(r => r.UpdateAvailable)} packages)" : "Update";
	}

	private void OnMenuEnable(object sender, RoutedEventArgs e) => SetPackagesEnabled(true);

	private void OnMenuDisable(object sender, RoutedEventArgs e) => SetPackagesEnabled(false);

	private void SetPackagesEnabled(bool on)
	{
		List<PackageRow> rows = Selected.Where(r => r.CanToggle).ToList();
		List<InstalledMod> mods = rows.SelectMany(r => r.Package.Own).DistinctBy(m => m.Key).ToList();
		if (mods.Count > 0)
		{
			SetEnabled(mods, on, rows.Count == 1 ? rows[0].Name : $"{rows.Count} packages");
		}
	}

	private void OnShowArchive(object sender, RoutedEventArgs e)
	{
		if (Selected.FirstOrDefault()?.Package.Record?.Archive is string a && File.Exists(a))
		{
			Process.Start("explorer.exe", $"/select,\"{a}\"");
		}
	}

	private void OnCopyName(object sender, RoutedEventArgs e) => CopyText(string.Join(Environment.NewLine, Selected.Select(r => r.Name)));

	private void OnCopyIds(object sender, RoutedEventArgs e) =>
		CopyText(string.Join(Environment.NewLine, Selected.SelectMany(r => r.Package.Members).Select(m => m.Manifest.Identifier ?? m.Key).Distinct()));

	private void OnMemberMenuOpening(object sender, ContextMenuEventArgs e)
	{
		if (MembersGrid.SelectedItem is not MemberRow m)
		{
			e.Handled = true;
			return;
		}
		MemberMenuToggle.Header = m.Mod.Enabled == false ? "Enable" : "Disable";
		MemberMenuToggle.IsEnabled = m.CanToggle;
	}

	private void OnMemberMenuToggle(object sender, RoutedEventArgs e)
	{
		if (MembersGrid.SelectedItem is MemberRow m)
		{
			SetEnabled([m.Mod], m.Mod.Enabled == false, m.Name);
		}
	}

	private void OnCopyMemberId(object sender, RoutedEventArgs e)
	{
		if (MembersGrid.SelectedItem is MemberRow m)
		{
			CopyText(m.Key);
		}
	}

	private void CopyText(string text)
	{
		if (text.Length == 0)
		{
			return;
		}
		try
		{
			Clipboard.SetText(text);
		}
		catch (System.Runtime.InteropServices.COMException)
		{
			// The clipboard is busy (another program holds it): try once more.
			Clipboard.SetDataObject(text, true);
		}
	}

	// ---- folders, settings ----

	private void OnRefresh(object sender, RoutedEventArgs e)
	{
		_state = StateStore.Load();
		Refresh();
	}

	private void OnOpenMods(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", $"\"{_settings.ModsDir}\"");

	private void OnOpenArchive(object sender, RoutedEventArgs e)
	{
		Directory.CreateDirectory(_settings.ArchiveDir);
		Process.Start("explorer.exe", $"\"{_settings.ArchiveDir}\"");
	}

	private void OnSettings(object sender, RoutedEventArgs e)
	{
		SettingsWindow w = new(_settings) { Owner = this };
		if (w.ShowDialog() == true)
		{
			_settings.Save();
			Log("Settings saved.");
			Refresh();
		}
	}
}

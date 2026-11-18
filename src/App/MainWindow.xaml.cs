using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TetzModManager.App;

/// <summary>A row of the list: what the mod says about itself, with its Nexus details behind it.</summary>
public sealed class ModRow(InstalledMod mod)
{
	public InstalledMod Mod { get; } = mod;
	public string Name => Mod.Manifest.Name;
	public string Version => Mod.InstalledVersion != Mod.Manifest.Version ? $"{Mod.Manifest.Version} ({Mod.InstalledVersion})" : Mod.Manifest.Version;
	public string Latest => Mod.Record?.LatestVersion ?? "";
	public string Author => Mod.Manifest.Author;
	public string Key => Mod.Manifest.Identifier ?? Mod.Key;
	public string NexusId => Mod.Record?.NexusModId?.ToString() ?? "";
	public bool? Enabled => Mod.LoadedAsDependency ? true : Mod.Enabled;
	public int EnabledSort => Enabled switch { true => 0, null => 1, false => 2 };
	public bool CanToggle => Mod.InPreferences;
	public string EnabledTip => Mod.LoadedAsDependency ? "Loaded because an enabled mod requires it" : Mod.InPreferences ? "Enabled in Preferences.json" : "Not a game mod entry";
	public bool IsDisabled => Mod.Enabled == false;
	public bool UpdateAvailable => Mod.UpdateAvailable;
	public bool HasWarning => Mod.Notes.Any(n => n != "Deployed by Vortex");
	public string NotesText => string.Join("; ", Mod.Notes);
}

public partial class MainWindow : Window
{
	private readonly Settings _settings = Settings.Load();
	private StateStore _state = StateStore.Load();
	private List<ModRow> _rows = [];
	private readonly DispatcherTimer _gameTimer = new() { Interval = TimeSpan.FromSeconds(3) };
	private bool _busy;

	public MainWindow()
	{
		InitializeComponent();
		_gameTimer.Tick += (_, _) => ShowGameStatus();
		_gameTimer.Start();
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		if (!File.Exists(Path.Combine(Settings.DataDir, "settings.json")))
		{
			_settings.Save();
		}
		Refresh();
		ShowGameStatus();
		Log($"Mods: {_settings.ModsDir}");
		Log($"Archive: {_settings.ArchiveDir}");
		if (NxmHandler.IsOurs())
		{
			Log("Nexus \"Mod Manager Download\" links come here.");
		}
		// Tests: TETZMM_SNAPSHOT=<png> renders the window to a file and closes (TETZMM_SELECT picks a row first).
		if (Environment.GetEnvironmentVariable("TETZMM_SNAPSHOT") is string shot)
		{
			if (Environment.GetEnvironmentVariable("TETZMM_SELECT") is string sel)
			{
				Grid.SelectedItem = Grid.Items.Cast<ModRow>().FirstOrDefault(r => r.Name.Contains(sel, StringComparison.OrdinalIgnoreCase));
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
		string? selected = (Grid.SelectedItem as ModRow)?.Mod.Key;
		_rows = Scanner.Scan(_settings, prefs, _state).Select(m => new ModRow(m)).ToList();
		ApplyFilter();
		if (selected != null)
		{
			Grid.SelectedItem = Grid.Items.Cast<ModRow>().FirstOrDefault(r => r.Mod.Key == selected);
		}
		int updates = _rows.Count(r => r.UpdateAvailable);
		Counts.Text = $"{_rows.Count} mods, {_rows.Count(r => r.Mod.Enabled == true)} enabled, {updates} updates, {_rows.Count(r => r.HasWarning)} with notes";
		UpdateAllButton.IsEnabled = updates > 0;
		UpdateAllButton.Content = updates > 0 ? $"Update all ({updates})" : "Update all";
		ShowDetails();
	}

	private void ApplyFilter()
	{
		string f = FilterBox?.Text?.Trim() ?? "";
		int view = ViewBox?.SelectedIndex ?? 0;
		IEnumerable<ModRow> rows = _rows;
		if (f.Length > 0)
		{
			rows = rows.Where(r => r.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Key.Contains(f, StringComparison.OrdinalIgnoreCase)
				|| r.Author.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Mod.FolderName.Contains(f, StringComparison.OrdinalIgnoreCase));
		}
		rows = view switch
		{
			1 => rows.Where(r => r.UpdateAvailable),
			2 => rows.Where(r => r.HasWarning),
			3 => rows.Where(r => r.Mod.Notes.Any(n => n.StartsWith("Duplicate") || n.StartsWith("Possible duplicate"))),
			4 => rows.Where(r => r.IsDisabled),
			5 => rows.Where(r => r.Mod.Record?.NexusModId == null),
			6 => rows.Where(r => r.Mod.VortexManaged),
			_ => rows,
		};
		if (Grid != null)
		{
			Grid.ItemsSource = rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
		}
	}

	private void OnFilterChanged(object sender, EventArgs e) => ApplyFilter();

	private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetails();

	private List<ModRow> Selected => Grid.SelectedItems.Cast<ModRow>().ToList();

	private void ShowDetails()
	{
		List<ModRow> sel = Selected;
		ModRow? row = sel.Count == 1 ? sel[0] : null;
		UpdateButton.IsEnabled = sel.Any(r => r.UpdateAvailable);
		NexusButton.IsEnabled = row?.Mod.Record?.NexusModId != null;
		LinkButton.IsEnabled = row?.Mod.HasManifest == true;
		FolderButton.IsEnabled = row != null;
		RollBackButton.IsEnabled = row != null && Installer.HasPrevious(row.Mod.Key);
		UninstallButton.IsEnabled = row != null;
		if (row == null)
		{
			Details.Text = sel.Count > 1 ? $"{sel.Count} mods selected" : "";
			return;
		}
		InstalledMod m = row.Mod;
		ModRecord? r = m.Record;
		List<string> lines =
		[
			$"{m.Manifest.Name} {m.Manifest.Version}" + (m.Manifest.Author.Length > 0 ? $" by {m.Manifest.Author}" : ""),
			$"Identifier: {m.Key}",
		];
		if (m.Manifest.Description.Length > 0)
		{
			lines.Add("");
			lines.Add(m.Manifest.Description);
			lines.Add("");
		}
		lines.Add($"Folder: {m.Folder}" + (m.VortexManaged ? " (deployed by Vortex)" : ""));
		lines.Add(m.InPreferences ? $"Preferences.json: {(m.Enabled == true ? "enabled" : "disabled")}, version {m.PreferencesVersion}"
			: m.LoadedAsDependency ? "Preferences.json: no entry; loaded because an enabled mod requires it" : m.Manifest.Identifier != null ? "Preferences.json: no entry" : "");
		if (m.Manifest.Required.Count > 0)
		{
			lines.Add("Requires: " + string.Join(", ", m.Manifest.Required));
		}
		if (m.Manifest.Optional.Count > 0)
		{
			lines.Add("Optional: " + string.Join(", ", m.Manifest.Optional));
		}
		if (m.Manifest.Incompatible.Count > 0)
		{
			lines.Add("Incompatible: " + string.Join(", ", m.Manifest.Incompatible));
		}
		if (r?.NexusModId != null)
		{
			lines.Add($"Nexus: mod {r.NexusModId}" + (r.NexusFileId != null ? $", file {r.NexusFileId}" : "") + (r.InstalledNexusVersion != null ? $", Nexus version {r.InstalledNexusVersion}" : "")
				+ (r.LatestVersion != null ? $"; newest {r.LatestVersion}" + (r.CheckedAt != null ? $" (checked {r.CheckedAt:g})" : "") : "") + $" [linked by {r.LinkSource}]");
		}
		if (r?.Archive != null)
		{
			lines.Add($"Archive: {r.Archive}" + (File.Exists(r.Archive) ? "" : " (missing)"));
		}
		if (m.Notes.Count > 0)
		{
			lines.Add("");
			lines.AddRange(m.Notes.Select(n => "• " + n));
		}
		Details.Text = string.Join(Environment.NewLine, lines.Where(l => l != null));
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
	}

	// ---- running work off the UI thread ----

	private async Task Run(string what, Func<Task> work)
	{
		if (_busy)
		{
			Log($"Busy: {what} waits for the current job.");
			return;
		}
		_busy = true;
		Progress.Visibility = Visibility.Visible;
		Progress.IsIndeterminate = true;
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
			_busy = false;
			Progress.Visibility = Visibility.Collapsed;
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

	/// <summary>Installs an archive. Asks first only when something needs a look: a Vortex folder, an older version, a blocked folder.</summary>
	private async Task InstallArchive(string path, NexusSource? nexus, bool confirmAlways)
	{
		List<InstalledMod> installed = Scanner.Scan(_settings, null, _state);
		InstallPlan plan = await Task.Run(() => Installer.Plan(_settings, path, installed));
		try
		{
			List<string> lines = [];
			bool ask = confirmAlways;
			foreach (PlannedMod p in plan.Mods)
			{
				string folder = Path.GetFileName(p.Target);
				if (p.Blocked != null)
				{
					lines.Add($"✗ {p.Manifest.Name}: {p.Blocked} It will be skipped.");
					ask = true;
					continue;
				}
				if (p.Existing == null)
				{
					lines.Add($"+ {p.Manifest.Name} {p.Manifest.Version} (new, folder \"{folder}\")");
				}
				else
				{
					int c = Versions.Compare(p.Manifest.Version, p.Existing.Manifest.Version);
					string how = c > 0 ? "update" : c < 0 ? "OLDER than the installed one" : "same version, reinstall";
					lines.Add($"↻ {p.Manifest.Name} {p.Existing.Manifest.Version} → {p.Manifest.Version} ({how}, folder \"{folder}\")");
					ask |= c < 0;
				}
				if (p.ReplacesVortexFolder)
				{
					lines.Add("   This folder is deployed by Vortex: Vortex will report it as changed until Railroader is removed from Vortex.");
					ask = true;
				}
			}
			if (ask && MessageBox.Show(this, $"Install from {Path.GetFileName(path)}:\n\n{string.Join("\n", lines)}\n\nGo ahead?",
				"Install", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
			{
				Log($"Not installed: {Path.GetFileName(path)}");
				return;
			}
			await Task.Run(() => Installer.Apply(_settings, _state, plan, nexus, Log));
		}
		finally
		{
			plan.Dispose();
		}
	}

	private async Task DownloadAndInstall(NexusClient nexus, int modId, int fileId, string? key, string? expires, bool confirmAlways)
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
			await InstallArchive(dest, new NexusSource(modId, fileId, file.FileName, file.Version), confirmAlways);
		}
		finally
		{
			File.Delete(dest);
		}
	}

	/// <summary>A start argument or a forwarded one: an nxm link or an archive path.</summary>
	public async void HandleArgument(string arg)
	{
		if (arg.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
		{
			await HandleNxm(arg);
		}
		else if (File.Exists(arg))
		{
			if (GameClosedOrSay())
			{
				await Run("Install", () => InstallArchive(arg, null, false));
			}
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
		await Run("Nexus download", () => DownloadAndInstall(nexus, link.ModId, link.FileId, link.Key, link.Expires, false));
	}

	private async void OnInstallArchive(object sender, RoutedEventArgs e)
	{
		OpenFileDialog dlg = new() { Filter = "Mod archives|*.zip;*.7z;*.rar|All files|*.*", Multiselect = true };
		if (dlg.ShowDialog(this) != true || !GameClosedOrSay())
		{
			return;
		}
		await Run("Install", async () =>
		{
			foreach (string f in dlg.FileNames)
			{
				await InstallArchive(f, null, false);
			}
		});
	}

	private void OnDragOver(object sender, DragEventArgs e)
	{
		e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
		e.Handled = true;
	}

	private async void OnDrop(object sender, DragEventArgs e)
	{
		if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || !GameClosedOrSay())
		{
			return;
		}
		await Run("Install", async () =>
		{
			foreach (string f in files.Where(File.Exists))
			{
				await InstallArchive(f, null, false);
			}
		});
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
			List<InstalledMod> mods = Scanner.Scan(_settings, null, _state);
			Log($"Checking {mods.Count(m => m.Record?.NexusModId != null)} linked mods on Nexus...");
			int n = await Updates.Check(nexus, _state, mods, Log);
			Log(n == 0 ? "Everything is up to date." : $"{n} updates.");
		});
	}

	private async Task UpdateRows(List<ModRow> rows)
	{
		rows = rows.Where(r => r.UpdateAvailable && r.Mod.Record?.LatestFileId != null).ToList();
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
			foreach (ModRow r in rows)
			{
				await DownloadAndInstall(nexus, r.Mod.Record!.NexusModId!.Value, r.Mod.Record.LatestFileId!.Value, null, null, false);
			}
		});
	}

	private async void OnUpdateSelected(object sender, RoutedEventArgs e) => await UpdateRows(Selected);

	private async void OnUpdateAll(object sender, RoutedEventArgs e)
	{
		List<ModRow> rows = _rows.Where(r => r.UpdateAvailable).ToList();
		if (MessageBox.Show(this, "Update these mods from Nexus?\n\n" + string.Join("\n", rows.Select(r => $"{r.Name}  {r.Mod.InstalledVersion} → {r.Latest}")),
			"Update all", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
		{
			await UpdateRows(rows);
		}
	}

	private async void OnVortexImport(object sender, RoutedEventArgs e)
	{
		await Run("Link from Vortex", () => Task.Run(() =>
		{
			int n = VortexImport.Run(_settings, _state, Scanner.Scan(_settings, null, _state), Log);
			Log($"{n} mods newly linked to Nexus. \"Check for updates\" matches each to its Nexus file.");
		}));
	}

	// ---- per mod ----

	private void OnToggleEnabled(object sender, RoutedEventArgs e)
	{
		if (sender is not CheckBox { DataContext: ModRow row } box || row.Mod.Manifest.Identifier is not string id)
		{
			return;
		}
		bool want = box.IsChecked == true;
		if (!GameClosedOrSay())
		{
			box.IsChecked = !want;
			return;
		}
		try
		{
			Preferences prefs = Preferences.Load(_settings.PreferencesPath);
			prefs.SetEnabled(id, want);
			prefs.Save();
			Log($"{(want ? "Enabled" : "Disabled")} {row.Name}");
		}
		catch (Exception ex) when (ex is IOException or InvalidOperationException)
		{
			Log(ex.Message);
		}
		Refresh();
	}

	private void OnNexusPage(object sender, RoutedEventArgs e)
	{
		if (Selected.FirstOrDefault()?.Mod.Record?.NexusModId is int id)
		{
			Process.Start(new ProcessStartInfo($"https://www.nexusmods.com/{NexusClient.GameDomain}/mods/{id}") { UseShellExecute = true });
		}
	}

	private void OnLink(object sender, RoutedEventArgs e)
	{
		if (Selected.FirstOrDefault() is not ModRow row)
		{
			return;
		}
		string current = row.Mod.Record?.NexusModId?.ToString() ?? "";
		string? answer = InputDialog.Ask(this, "Link to Nexus", $"Nexus mod id (or page URL) for {row.Name}.\nLeave empty to unlink.", current);
		if (answer == null)
		{
			return;
		}
		System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(answer, @"(?:mods/)?(\d+)\s*$");
		ModRecord r = _state.Get(row.Mod.Key);
		if (answer.Trim().Length == 0)
		{
			r.NexusModId = null;
			r.NexusFileId = null;
			r.LatestVersion = null;
			r.LatestFileId = null;
			Log($"Unlinked {row.Name} from Nexus");
		}
		else if (m.Success)
		{
			r.NexusModId = int.Parse(m.Groups[1].Value);
			r.NexusFileId = null;
			r.LatestVersion = null;
			r.LatestFileId = null;
			r.LinkSource = "manual";
			Log($"Linked {row.Name} to Nexus mod {r.NexusModId}");
		}
		_state.Save();
		Refresh();
	}

	private void OnOpenFolder(object sender, RoutedEventArgs e)
	{
		if (Selected.FirstOrDefault() is ModRow row)
		{
			Process.Start("explorer.exe", $"\"{row.Mod.Folder}\"");
		}
	}

	private async void OnRollBack(object sender, RoutedEventArgs e)
	{
		if (Selected.FirstOrDefault() is not ModRow row || !GameClosedOrSay())
		{
			return;
		}
		if (MessageBox.Show(this, $"Put back the version of {row.Name} that was installed before {row.Mod.Manifest.Version}?", "Roll back",
			MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
		{
			await Run("Roll back", () => Task.Run(() => Installer.RollBack(_settings, _state, row.Mod, Log)));
		}
	}

	private async void OnUninstall(object sender, RoutedEventArgs e)
	{
		if (Selected.FirstOrDefault() is not ModRow row || !GameClosedOrSay())
		{
			return;
		}
		if (MessageBox.Show(this, $"Uninstall {row.Name} {row.Mod.Manifest.Version}?\n\nIts folder moves out of Mods (kept for rollback) and its Preferences.json entry is removed. Its archive stays.",
			"Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
		{
			await Run("Uninstall", () => Task.Run(() => Installer.Uninstall(_settings, _state, row.Mod, Log)));
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

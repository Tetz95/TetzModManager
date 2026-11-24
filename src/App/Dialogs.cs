using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace TetzModManager.App;

/// <summary>A one-line question.</summary>
public static class InputDialog
{
	public static string? Ask(Window owner, string title, string prompt, string value)
	{
		Window w = new()
		{
			Title = title,
			Owner = owner,
			Icon = owner.Icon,
			Width = 460,
			SizeToContent = SizeToContent.Height,
			ResizeMode = ResizeMode.NoResize,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
		};
		TextBox box = new() { Text = value, Margin = new Thickness(0, 8, 0, 12) };
		Button ok = new() { Content = "OK", IsDefault = true, Width = 80 };
		Button cancel = new() { Content = "Cancel", IsCancel = true, Width = 80 };
		ok.Click += (_, _) => w.DialogResult = true;
		StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
		buttons.Children.Add(ok);
		buttons.Children.Add(cancel);
		StackPanel panel = new() { Margin = new Thickness(12) };
		panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
		panel.Children.Add(box);
		panel.Children.Add(buttons);
		w.Content = panel;
		w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
		return w.ShowDialog() == true ? box.Text : null;
	}
}

/// <summary>Folders, the Nexus key file, and the nxm link handler.</summary>
public sealed class SettingsWindow : Window
{
	private readonly Settings _settings;
	private readonly TextBox _home, _archive, _key, _vortex, _edits, _hours;
	private readonly CheckBox _autoCheck, _managerCheck;
	private readonly TextBlock _nxmStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };

	public SettingsWindow(Settings settings)
	{
		_settings = settings;
		Title = "Settings";
		Icon = Application.Current?.MainWindow?.Icon;
		Width = 720;
		SizeToContent = SizeToContent.Height;
		ResizeMode = ResizeMode.NoResize;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;

		Grid grid = new() { Margin = new Thickness(12) };
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		int row = 0;
		TextBox Field(string label, string value, bool file, string help)
		{
			grid.RowDefinitions.Add(new RowDefinition());
			grid.RowDefinitions.Add(new RowDefinition());
			TextBlock l = new() { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 8, 0) };
			TextBox t = new() { Text = value, Margin = new Thickness(0, 6, 6, 0) };
			Button b = new() { Content = "Browse...", Margin = new Thickness(0, 6, 0, 0) };
			b.Click += (_, _) =>
			{
				if (file)
				{
					OpenFileDialog d = new() { FileName = t.Text, Filter = "Text files|*.txt|All files|*.*" };
					if (d.ShowDialog(this) == true)
					{
						t.Text = d.FileName;
					}
				}
				else
				{
					OpenFolderDialog d = new() { InitialDirectory = System.IO.Directory.Exists(t.Text) ? t.Text : "" };
					if (d.ShowDialog(this) == true)
					{
						t.Text = d.FolderName;
					}
				}
			};
			TextBlock h = new() { Text = help, Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
			Grid.SetRow(l, row); Grid.SetRow(t, row); Grid.SetRow(b, row);
			Grid.SetColumn(t, 1); Grid.SetColumn(b, 2);
			Grid.SetRow(h, row + 1); Grid.SetColumn(h, 1);
			grid.Children.Add(l); grid.Children.Add(t); grid.Children.Add(b); grid.Children.Add(h);
			row += 2;
			return t;
		}
		(string detected, string from) = Steam.DetectHome();
		_home = Field("Game home folder", settings.HomeDir, false,
			$"Holds Mods and Preferences.json. Leave blank to follow the game: now {detected} ({from}).");
		_archive = Field("Archive folder", settings.ArchiveDir, false, "Every installed archive is kept here, the newest one per mod.");
		_key = Field("Nexus API key file", settings.NexusKeyFile, true, "A text file with your personal API key (nexusmods.com > Site preferences > API keys). Sent only to api.nexusmods.com. Optional: update checks work without one, downloading from Nexus here needs it.");
		_vortex = Field("Vortex downloads", settings.VortexDownloadsDir, false, "Vortex's Railroader download folder: used once to link installed mods to their Nexus pages.");
		_edits = Field("Local edits folder", settings.LocalEditsDir, false,
			"Optional: your own edits to other authors' mods, one folder per mod named like its folder in Mods. Those mods get a note, and updating them asks first.");

		_autoCheck = new CheckBox { Content = "Check Nexus for mod updates when the manager opens, and while it's open every", IsChecked = settings.AutoCheckUpdates, VerticalAlignment = VerticalAlignment.Center };
		_hours = new TextBox { Text = settings.CheckUpdatesEveryHours.ToString(), Width = 36, Margin = new Thickness(6, 0, 6, 0) };
		StackPanel auto = new() { Orientation = Orientation.Horizontal };
		auto.Children.Add(_autoCheck);
		auto.Children.Add(_hours);
		auto.Children.Add(new TextBlock { Text = "hours", VerticalAlignment = VerticalAlignment.Center });
		_managerCheck = new CheckBox { Content = "Look for a new version of Tetz's Mod Manager when it opens (GitHub)", IsChecked = settings.CheckManagerUpdates, Margin = new Thickness(0, 6, 0, 0) };
		StackPanel updates = new() { Margin = new Thickness(0, 14, 0, 0) };
		updates.Children.Add(new TextBlock { Text = "Updates", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
		updates.Children.Add(auto);
		updates.Children.Add(_managerCheck);
		updates.Children.Add(new TextBlock { Text = "Nothing installs by itself: updates wait for Update or Update all. Check for updates also looks for a new manager.", Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) });

		Button take = new() { Content = "Handle Nexus links here" };
		Button back = new() { Content = "Give Nexus links back" };
		take.Click += (_, _) => { Save(); NxmHandler.TakeOver(_settings); ShowNxm(); };
		back.Click += (_, _) =>
		{
			try
			{
				NxmHandler.GiveBack(_settings);
			}
			catch (InvalidOperationException e)
			{
				MessageBox.Show(this, e.Message, "Nexus links");
			}
			ShowNxm();
		};
		StackPanel nxm = new() { Margin = new Thickness(0, 14, 0, 0) };
		nxm.Children.Add(new TextBlock { Text = "Nexus \"Mod Manager Download\" links (nxm://)", FontWeight = FontWeights.SemiBold });
		nxm.Children.Add(_nxmStatus);
		StackPanel nb = new() { Orientation = Orientation.Horizontal };
		nb.Children.Add(take);
		nb.Children.Add(back);
		nxm.Children.Add(nb);
		ShowNxm();

		Button ok = new() { Content = "Save", IsDefault = true, Width = 80 };
		Button cancel = new() { Content = "Cancel", IsCancel = true, Width = 80 };
		ok.Click += (_, _) => { Save(); DialogResult = true; };
		StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
		buttons.Children.Add(ok);
		buttons.Children.Add(cancel);

		StackPanel root = new() { Margin = new Thickness(12) };
		root.Children.Add(grid);
		root.Children.Add(updates);
		root.Children.Add(nxm);
		root.Children.Add(buttons);
		Content = root;
		WindowPlaces.Track(this, "settings", size: false);
	}

	private void Save()
	{
		_settings.HomeDir = _home.Text.Trim();
		_settings.ArchiveDir = _archive.Text.Trim();
		_settings.NexusKeyFile = _key.Text.Trim();
		_settings.VortexDownloadsDir = _vortex.Text.Trim();
		_settings.LocalEditsDir = _edits.Text.Trim();
		_settings.AutoCheckUpdates = _autoCheck.IsChecked == true;
		_settings.CheckUpdatesEveryHours = int.TryParse(_hours.Text.Trim(), out int h) && h >= 1 ? h : _settings.CheckUpdatesEveryHours;
		_settings.CheckManagerUpdates = _managerCheck.IsChecked == true;
		_settings.Save();
	}

	private void ShowNxm()
	{
		string? cmd = NxmHandler.CurrentCommand();
		_nxmStatus.Text = NxmHandler.IsOurs()
			? "Handled here. Links for other games go to: " + (_settings.PreviousNxmCommand.Length > 0 ? _settings.PreviousNxmCommand : "(nothing recorded)")
			: "Handled by: " + (cmd ?? "nothing") + ". Taking them over keeps this command for other games' links, and \"Give back\" restores it.";
	}
}

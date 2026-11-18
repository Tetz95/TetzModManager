using System.IO;
using System.Text.Json;
using System.Windows;

namespace TetzModManager.App;

/// <summary>Where a window was when it closed (device-independent units, as WPF uses).</summary>
public sealed class WindowPlace
{
	public double Left { get; set; }
	public double Top { get; set; }
	public double Width { get; set; }
	public double Height { get; set; }
	public bool Maximized { get; set; }
	/// <summary>Window-specific extras (splitter positions).</summary>
	public Dictionary<string, double> Extra { get; set; } = [];
}

/// <summary>
/// Remembers each window's size and position in %APPDATA%\TetzModManager\windows.json. A saved place that is no longer
/// on any screen (a monitor unplugged) is ignored. Nothing is saved in snapshot runs (tests).
/// </summary>
public static class WindowPlaces
{
	private static string FilePath => Path.Combine(Settings.DataDir, "windows.json");
	private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
	private static bool Snapshot => Environment.GetEnvironmentVariable("TETZMM_SNAPSHOT") != null;

	private static Dictionary<string, WindowPlace> Load()
	{
		try
		{
			if (File.Exists(FilePath))
			{
				return JsonSerializer.Deserialize<Dictionary<string, WindowPlace>>(File.ReadAllText(FilePath)) ?? [];
			}
		}
		catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
		{
		}
		return [];
	}

	public static WindowPlace? Get(string key) => Load().GetValueOrDefault(key);

	/// <summary>
	/// Puts the window where it was (call before it is shown) and saves its place when it closes. Without
	/// <paramref name="size"/> only the position and width are restored (windows that size to their content).
	/// </summary>
	public static void Track(Window w, string key, bool size = true, Action<WindowPlace>? saveExtra = null)
	{
		if (!Snapshot && Get(key) is WindowPlace p && OnScreen(p))
		{
			w.WindowStartupLocation = WindowStartupLocation.Manual;
			w.Left = p.Left;
			w.Top = p.Top;
			w.Width = Math.Max(p.Width, w.MinWidth);
			if (size)
			{
				w.Height = Math.Max(p.Height, w.MinHeight);
				if (p.Maximized)
				{
					// Maximize once shown, so it maximizes on the screen it was placed on.
					w.SourceInitialized += (_, _) => w.WindowState = WindowState.Maximized;
				}
			}
		}
		w.Closing += (_, _) =>
		{
			if (Snapshot)
			{
				return;
			}
			Rect r = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight) : w.RestoreBounds;
			if (r.IsEmpty || r.Width <= 0)
			{
				return;
			}
			WindowPlace place = new() { Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height, Maximized = w.WindowState == WindowState.Maximized };
			saveExtra?.Invoke(place);
			Dictionary<string, WindowPlace> all = Load();
			all[key] = place;
			try
			{
				Directory.CreateDirectory(Settings.DataDir);
				File.WriteAllText(FilePath, JsonSerializer.Serialize(all, Json));
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
			}
		};
	}

	/// <summary>At least a usable part of the title bar lies within the desktop spanning all screens.</summary>
	private static bool OnScreen(WindowPlace p)
	{
		Rect desktop = new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
			SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
		Rect titleBar = new(p.Left, p.Top, Math.Max(p.Width, 1), 30);
		titleBar.Intersect(desktop);
		return p.Width > 100 && p.Height > 50 && !titleBar.IsEmpty && titleBar.Width >= 100;
	}
}

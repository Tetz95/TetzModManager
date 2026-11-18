using System.Text.Json;

namespace TetzModManager;

/// <summary>User settings, kept in %APPDATA%\TetzModManager\settings.json.</summary>
public sealed class Settings
{
	/// <summary>The game's home folder: Mods and Preferences.json live here.</summary>
	public string HomeDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Railroader");

	/// <summary>Where downloaded and installed zips are kept, at most one per mod.</summary>
	public string ArchiveDir { get; set; } = @"D:\Railroader Mod Archive";

	/// <summary>A text file holding the Nexus Mods API key (never shown or logged).</summary>
	public string NexusKeyFile { get; set; } = "";

	/// <summary>Vortex's download folder for Railroader, read once to link installed mods to Nexus.</summary>
	public string VortexDownloadsDir { get; set; } = @"D:\Vortex Mods\Downloads\railroader";

	/// <summary>The nxm handler command that was registered before ours (Vortex's), for links of other games.</summary>
	public string PreviousNxmCommand { get; set; } = "";

	/// <summary>The game's process name, checked before anything in Mods or Preferences.json is changed.</summary>
	public string GameProcess { get; set; } = "Railroader";

	public string ModsDir => Path.Combine(HomeDir, "Mods");
	public string PreferencesPath => Path.Combine(HomeDir, "Preferences.json");

	// TETZMM_DATA moves all of the tool's own files (settings, state, rollback copies) elsewhere: tests.
	private static string? Override => Environment.GetEnvironmentVariable("TETZMM_DATA");
	public static string DataDir => Override ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TetzModManager");
	public static string PreviousDir => Path.Combine(Override ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TetzModManager"), "previous");
	public static string WorkDir => Path.Combine(Override ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TetzModManager"), "work");
	private static string FilePath => Path.Combine(DataDir, "settings.json");

	internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

	public static Settings Load()
	{
		try
		{
			if (File.Exists(FilePath))
			{
				return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
			}
		}
		catch (JsonException)
		{
		}
		return new Settings();
	}

	public void Save()
	{
		Directory.CreateDirectory(DataDir);
		File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
	}

	public string? ReadNexusKey()
	{
		if (string.IsNullOrWhiteSpace(NexusKeyFile) || !File.Exists(NexusKeyFile))
		{
			return null;
		}
		string key = File.ReadAllText(NexusKeyFile).Trim();
		return key.Length > 0 ? key : null;
	}
}

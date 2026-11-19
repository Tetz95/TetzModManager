using System.Text.Json;

namespace TetzModManager;

/// <summary>User settings, kept in %APPDATA%\TetzModManager\settings.json.</summary>
public sealed class Settings
{
	/// <summary>
	/// The game's home folder (Mods, Preferences.json) when set here; blank follows the game: Steam's -homedir launch
	/// option, else Documents\Railroader.
	/// </summary>
	public string HomeDir { get; set; } = "";

	/// <summary>The home folder in use: <see cref="HomeDir"/> when set, else detected (read once per run).</summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public string GameHome => string.IsNullOrWhiteSpace(HomeDir) ? (_detected ??= Steam.DetectHome().Path) : HomeDir;

	private string? _detected;

	/// <summary>Where downloaded and installed zips are kept, at most one per mod.</summary>
	public string ArchiveDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Railroader Mod Archive");

	/// <summary>A text file holding the Nexus Mods API key (never shown or logged).</summary>
	public string NexusKeyFile { get; set; } = "";

	/// <summary>Vortex's download folder for Railroader, read once to link installed mods to Nexus.</summary>
	public string VortexDownloadsDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vortex", "downloads", "railroader");

	/// <summary>The nxm handler command that was registered before ours (Vortex's), for links of other games.</summary>
	public string PreviousNxmCommand { get; set; } = "";

	/// <summary>The game's process name, checked before anything in Mods or Preferences.json is changed.</summary>
	public string GameProcess { get; set; } = "Railroader";

	/// <summary>Check Nexus for mod updates when the manager opens, and every <see cref="CheckUpdatesEveryHours"/> while it's open.</summary>
	public bool AutoCheckUpdates { get; set; } = true;

	public int CheckUpdatesEveryHours { get; set; } = 6;

	/// <summary>When Nexus was last asked for updates (automatic checks wait at least an hour after any check).</summary>
	public DateTime? LastUpdateCheck { get; set; }

	/// <summary>Look for a newer release of the manager itself on GitHub when it opens.</summary>
	public bool CheckManagerUpdates { get; set; } = true;

	public DateTime? LastManagerCheck { get; set; }

	/// <summary>A manager release the user chose to skip: not offered again on opening.</summary>
	public string SkippedManagerVersion { get; set; } = "";

	/// <summary>
	/// A folder of your own edits to other authors' mods, one folder per mod named like its folder in Mods. Mods with one
	/// get a note, and updating them asks first: an update replaces the edited files.
	/// </summary>
	public string LocalEditsDir { get; set; } = "";

	public string ModsDir => Path.Combine(GameHome, "Mods");
	public string PreferencesPath => Path.Combine(GameHome, "Preferences.json");

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

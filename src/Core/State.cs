using System.Text.Json;

namespace TetzModManager;

/// <summary>What the tool knows about a mod beyond its manifest: its Nexus page and file, and its archived zip.</summary>
public sealed class ModRecord
{
	public int? NexusModId { get; set; }
	/// <summary>The Nexus file the installed version came from, when known (an nxm install, or matched by version).</summary>
	public int? NexusFileId { get; set; }
	public string? NexusFileName { get; set; }
	/// <summary>The Nexus file version of the installed copy, when the author's Info.json version differs from it.</summary>
	public string? InstalledNexusVersion { get; set; }
	/// <summary>Where the link came from: "nxm", "vortex", "manual".</summary>
	public string? LinkSource { get; set; }

	public string? LatestVersion { get; set; }
	public int? LatestFileId { get; set; }
	public DateTime? CheckedAt { get; set; }

	/// <summary>The archived zip this mod was installed from.</summary>
	public string? Archive { get; set; }
	public string? InstalledVersion { get; set; }
	public DateTime? InstalledAt { get; set; }
}

/// <summary>%APPDATA%\TetzModManager\state.json: one record per mod key.</summary>
public sealed class StateStore
{
	public Dictionary<string, ModRecord> Mods { get; set; } = new(StringComparer.OrdinalIgnoreCase);

	private static string FilePath => Path.Combine(Settings.DataDir, "state.json");

	public ModRecord Get(string key)
	{
		if (!Mods.TryGetValue(key, out ModRecord? r))
		{
			r = new ModRecord();
			Mods[key] = r;
		}
		return r;
	}

	public ModRecord? Find(string key) => Mods.TryGetValue(key, out ModRecord? r) ? r : null;

	public static StateStore Load()
	{
		try
		{
			if (File.Exists(FilePath))
			{
				StateStore? s = JsonSerializer.Deserialize<StateStore>(File.ReadAllText(FilePath), Settings.Json);
				if (s != null)
				{
					s.Mods = new Dictionary<string, ModRecord>(s.Mods, StringComparer.OrdinalIgnoreCase);
					return s;
				}
			}
		}
		catch (JsonException)
		{
		}
		return new StateStore();
	}

	public void Save()
	{
		Directory.CreateDirectory(Settings.DataDir);
		string tmp = FilePath + ".tmp";
		File.WriteAllText(tmp, JsonSerializer.Serialize(this, Settings.Json));
		File.Move(tmp, FilePath, true);
	}
}

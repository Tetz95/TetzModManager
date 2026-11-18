using System.Text;
using Microsoft.Win32;

namespace TetzModManager;

/// <summary>
/// Where Steam keeps Railroader: the game's home folder (Mods, Preferences.json) is the launch option
/// <c>-homedir &lt;path&gt;</c> when one is set in Steam, else <c>Documents\Railroader</c>; the install folder comes
/// from Steam's library list.
/// </summary>
public static class Steam
{
	public const string AppId = "1683150";

	public static string DefaultHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Railroader");

	public static string? SteamDir()
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}
		using RegistryKey? k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
		return k?.GetValue("SteamPath") is string p && Directory.Exists(p) ? Path.GetFullPath(p) : null;
	}

	/// <summary>Railroader's launch options from the most recently used Steam account's localconfig.vdf.</summary>
	public static string? LaunchOptions()
	{
		string? steam = SteamDir();
		string userdata = steam != null ? Path.Combine(steam, "userdata") : "";
		if (!Directory.Exists(userdata))
		{
			return null;
		}
		foreach (string cfg in Directory.EnumerateDirectories(userdata)
			.Select(d => Path.Combine(d, "config", "localconfig.vdf"))
			.Where(File.Exists)
			.OrderByDescending(File.GetLastWriteTimeUtc))
		{
			try
			{
				Dictionary<string, object> root = Vdf.Parse(File.ReadAllText(cfg));
				if (Vdf.Find(root, "UserLocalConfigStore", "Software", "Valve", "Steam", "apps", AppId) is Dictionary<string, object> app)
				{
					return app.TryGetValue("LaunchOptions", out object? o) ? o as string : "";
				}
			}
			catch (Exception e) when (e is IOException or FormatException)
			{
			}
		}
		return null;
	}

	/// <summary>The value of <c>-homedir</c> in launch options ("-homedir path", "-homedir \"a path\"" or "-homedir=path").</summary>
	public static string? HomeDirOption(string? launchOptions)
	{
		List<string> args = SplitArgs(launchOptions ?? "");
		for (int i = 0; i < args.Count; i++)
		{
			if (args[i].StartsWith("-homedir=", StringComparison.OrdinalIgnoreCase))
			{
				return args[i]["-homedir=".Length..];
			}
			if (args[i].Equals("-homedir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
			{
				return args[i + 1];
			}
		}
		return null;
	}

	/// <summary>The game's home folder as the game will use it, and where that came from.</summary>
	public static (string Path, string From) DetectHome()
	{
		string? option = HomeDirOption(LaunchOptions());
		return string.IsNullOrWhiteSpace(option) ? (DefaultHome, "the game's default") : (option, "Steam launch option -homedir");
	}

	/// <summary>The install folder, from Steam's library folders and the app manifest.</summary>
	public static string? GameDir()
	{
		string? steam = SteamDir();
		if (steam == null)
		{
			return null;
		}
		List<string> libraries = [steam];
		string list = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
		try
		{
			if (File.Exists(list) && Vdf.Parse(File.ReadAllText(list)).GetValueOrDefault("libraryfolders") is Dictionary<string, object> folders)
			{
				libraries.AddRange(folders.Values.OfType<Dictionary<string, object>>().Select(f => f.GetValueOrDefault("path") as string).OfType<string>());
			}
		}
		catch (Exception e) when (e is IOException or FormatException)
		{
		}
		foreach (string lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			string manifest = Path.Combine(lib, "steamapps", $"appmanifest_{AppId}.acf");
			try
			{
				if (File.Exists(manifest) && Vdf.Find(Vdf.Parse(File.ReadAllText(manifest)), "AppState", "installdir") is string dir)
				{
					string full = Path.Combine(lib, "steamapps", "common", dir);
					if (Directory.Exists(full))
					{
						return full;
					}
				}
			}
			catch (Exception e) when (e is IOException or FormatException)
			{
			}
		}
		return null;
	}

	private static List<string> SplitArgs(string s)
	{
		List<string> args = [];
		StringBuilder cur = new();
		bool quoted = false, any = false;
		foreach (char c in s)
		{
			if (c == '"')
			{
				quoted = !quoted;
				any = true;
			}
			else if (char.IsWhiteSpace(c) && !quoted)
			{
				if (any)
				{
					args.Add(cur.ToString());
				}
				cur.Clear();
				any = false;
			}
			else
			{
				cur.Append(c);
				any = true;
			}
		}
		if (any)
		{
			args.Add(cur.ToString());
		}
		return args;
	}
}

/// <summary>Valve's KeyValues text format: quoted keys, quoted values or { nested blocks }; keys case-insensitive.</summary>
public static class Vdf
{
	public static Dictionary<string, object> Parse(string text)
	{
		int i = 0;
		return Block(text, ref i, topLevel: true);
	}

	public static object? Find(Dictionary<string, object> root, params string[] path)
	{
		object? at = root;
		foreach (string key in path)
		{
			at = at is Dictionary<string, object> d && d.TryGetValue(key, out object? next) ? next : null;
		}
		return at;
	}

	private static Dictionary<string, object> Block(string t, ref int i, bool topLevel)
	{
		Dictionary<string, object> d = new(StringComparer.OrdinalIgnoreCase);
		while (true)
		{
			Skip(t, ref i);
			if (i >= t.Length)
			{
				return topLevel ? d : throw new FormatException("unclosed block");
			}
			if (t[i] == '}')
			{
				i++;
				return d;
			}
			string key = Token(t, ref i);
			Skip(t, ref i);
			if (i < t.Length && t[i] == '{')
			{
				i++;
				d[key] = Block(t, ref i, topLevel: false);
			}
			else
			{
				d[key] = Token(t, ref i);
			}
		}
	}

	private static void Skip(string t, ref int i)
	{
		while (i < t.Length)
		{
			if (char.IsWhiteSpace(t[i]))
			{
				i++;
			}
			else if (t[i] == '/' && i + 1 < t.Length && t[i + 1] == '/')
			{
				while (i < t.Length && t[i] != '\n')
				{
					i++;
				}
			}
			else
			{
				return;
			}
		}
	}

	private static string Token(string t, ref int i)
	{
		if (i >= t.Length)
		{
			throw new FormatException("unexpected end");
		}
		StringBuilder sb = new();
		if (t[i] != '"')
		{
			while (i < t.Length && !char.IsWhiteSpace(t[i]) && t[i] is not ('{' or '}' or '"'))
			{
				sb.Append(t[i++]);
			}
			return sb.ToString();
		}
		i++;
		while (i < t.Length && t[i] != '"')
		{
			if (t[i] == '\\' && i + 1 < t.Length)
			{
				char e = t[i + 1];
				sb.Append(e switch { 'n' => '\n', 't' => '\t', _ => e });
				i += 2;
			}
			else
			{
				sb.Append(t[i++]);
			}
		}
		i++;
		return sb.ToString();
	}
}

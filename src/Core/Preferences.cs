using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TetzModManager;

/// <summary>
/// The game's Preferences.json: which mods are enabled, with their version and scope. Entries live in "mods" (scope 0
/// unscoped, 1 optional) or "clientMods" (scope 2). Everything else in the file is kept as it is; the file is written
/// back with its own line endings and two-space indent, and a copy of the old file is kept first.
/// </summary>
public sealed class Preferences
{
	private readonly string _path;
	private readonly JsonObject _root;
	private readonly string _newline;
	private readonly bool _finalNewline;

	private Preferences(string path, JsonObject root, string newline, bool finalNewline)
	{
		_path = path;
		_root = root;
		_newline = newline;
		_finalNewline = finalNewline;
	}

	public static Preferences Load(string path)
	{
		string text = File.ReadAllText(path);
		JsonObject root = JsonNode.Parse(text.TrimStart('﻿'))?.AsObject() ?? throw new InvalidDataException("Preferences.json is empty.");
		return new Preferences(path, root, text.Contains("\r\n") ? "\r\n" : "\n", text.EndsWith('\n'));
	}

	public JsonObject? Find(string identifier)
	{
		foreach (string list in new[] { "mods", "clientMods" })
		{
			if (_root[list] is JsonArray a)
			{
				foreach (JsonNode? n in a)
				{
					if (n is JsonObject o && (string?)o["identifier"] == identifier)
					{
						return o;
					}
				}
			}
		}
		return null;
	}

	public bool? Enabled(string identifier) => Find(identifier)?["enabled"]?.GetValue<bool>();

	public string? Version(string identifier) => (string?)Find(identifier)?["version"];

	/// <summary>The game's "Allow DLL Mods" setting (off when the file doesn't have it).</summary>
	public bool AllowDllMods => _root["settings"]?["mods.allow-dll"] is JsonValue v
		&& (v.TryGetValue(out bool b) ? b : v.TryGetValue(out double n) && n != 0);

	public void SetEnabled(string identifier, bool enabled)
	{
		JsonObject o = Find(identifier) ?? throw new InvalidOperationException($"{identifier} isn't in Preferences.json.");
		o["enabled"] = enabled;
	}

	/// <summary>Sets the version of an existing entry, or adds the mod enabled (scope 0, or the client list for a client mod).</summary>
	public bool SetVersionOrAdd(string identifier, string version, string? manifestScope)
	{
		JsonObject? o = Find(identifier);
		if (o != null)
		{
			o["version"] = version;
			return false;
		}
		bool client = string.Equals(manifestScope, "client", StringComparison.OrdinalIgnoreCase);
		string list = client ? "clientMods" : "mods";
		if (_root[list] is not JsonArray a)
		{
			a = new JsonArray();
			_root[list] = a;
		}
		a.Add(new JsonObject
		{
			["identifier"] = identifier,
			["version"] = version,
			["enabled"] = true,
			["scope"] = client ? 2 : 0,
		});
		return true;
	}

	public bool Remove(string identifier)
	{
		foreach (string list in new[] { "mods", "clientMods" })
		{
			if (_root[list] is JsonArray a)
			{
				for (int i = 0; i < a.Count; i++)
				{
					if (a[i] is JsonObject o && (string?)o["identifier"] == identifier)
					{
						a.RemoveAt(i);
						return true;
					}
				}
			}
		}
		return false;
	}

	public string Serialize()
	{
		using MemoryStream ms = new();
		using (Utf8JsonWriter w = new(ms, new JsonWriterOptions
		{
			Indented = true,
			IndentSize = 2,
			NewLine = _newline,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		}))
		{
			_root.WriteTo(w);
		}
		string text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
		return _finalNewline ? text + _newline : text;
	}

	public void Save()
	{
		string backups = Path.Combine(Settings.DataDir, "preferences-backups");
		Directory.CreateDirectory(backups);
		File.Copy(_path, Path.Combine(backups, $"Preferences-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json"), true);
		foreach (FileInfo old in new DirectoryInfo(backups).GetFiles("Preferences-*.json").OrderByDescending(f => f.Name).Skip(20))
		{
			old.Delete();
		}
		string tmp = _path + ".tmp";
		File.WriteAllText(tmp, Serialize(), new System.Text.UTF8Encoding(false));
		File.Move(tmp, _path, true);
	}
}

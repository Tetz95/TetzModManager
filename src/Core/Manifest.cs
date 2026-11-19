using System.Text.Json;

namespace TetzModManager;

/// <summary>
/// What a mod says about itself: a 0.6 Info.json (identifier, name, version, dependencies) or an old Unity Mod Manager
/// info.json (Id, DisplayName, Version). <see cref="Key"/> is the mod's identity everywhere in this tool: the 0.6
/// identifier, or "umm:" + Id. Folder names and Nexus archive names never identify a mod.
/// </summary>
public sealed record ModManifest(
	string Key,
	string? Identifier,
	string Name,
	string Version,
	string Author,
	string Description,
	string? Scope,
	bool Umm,
	IReadOnlyList<string> Required,
	IReadOnlyList<string> Optional,
	IReadOnlyList<string> Incompatible,
	// A Unity Mod Manager mod's AssemblyName (a Legacy DLL mod). Without one (sound packs such as Reaper's: info.json +
	// Audio) the files are data: the game doesn't load them, but a 0.6 mod may read them (Rex's Tool Box does Reaper's).
	string? UmmAssembly = null)
{
	private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

	/// <summary>The manifest file in a folder (Info.json or info.json), or null.</summary>
	public static string? FindFile(string dir)
	{
		if (!Directory.Exists(dir))
		{
			return null;
		}
		foreach (string f in Directory.EnumerateFiles(dir))
		{
			if (string.Equals(Path.GetFileName(f), "info.json", StringComparison.OrdinalIgnoreCase))
			{
				return f;
			}
		}
		return null;
	}

	public static ModManifest? ReadFolder(string dir)
	{
		string? file = FindFile(dir);
		if (file == null)
		{
			return null;
		}
		try
		{
			return Parse(File.ReadAllText(file), Path.GetFileName(dir));
		}
		catch (Exception e) when (e is JsonException or IOException)
		{
			return null;
		}
	}

	public static ModManifest? Parse(string json, string fallbackName)
	{
		using JsonDocument doc = JsonDocument.Parse(json.TrimStart('﻿'), Options);
		JsonElement root = doc.RootElement;
		if (root.ValueKind != JsonValueKind.Object)
		{
			return null;
		}
		string? identifier = Str(root, "identifier");
		if (identifier != null)
		{
			JsonElement deps = Prop(root, "dependencies") ?? default;
			return new ModManifest(identifier, identifier,
				Str(root, "name") ?? fallbackName,
				Str(root, "version") ?? "",
				Str(root, "author") ?? "",
				Str(root, "description") ?? "",
				Str(root, "scope"),
				false,
				List(deps, "required"), List(deps, "optional"), List(deps, "incompatible"));
		}
		string? id = Str(root, "Id");
		if (id != null)
		{
			return new ModManifest("umm:" + id, null,
				Str(root, "DisplayName") ?? id,
				Str(root, "Version") ?? "",
				Str(root, "Author") ?? "",
				"",
				null,
				true,
				[], [], [],
				Str(root, "AssemblyName"));
		}
		return null;
	}

	/// <summary>"rr/game ==0.6.2" -> ("rr/game", "==0.6.2").</summary>
	public static (string Id, string Constraint) SplitDependency(string dep)
	{
		dep = dep.Trim();
		int i = dep.IndexOfAny([' ', '=', '>', '<', '~', '^']);
		return i < 0 ? (dep, "") : (dep[..i], dep[i..].Trim());
	}

	private static JsonElement? Prop(JsonElement e, string name)
	{
		if (e.ValueKind != JsonValueKind.Object)
		{
			return null;
		}
		foreach (JsonProperty p in e.EnumerateObject())
		{
			if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
			{
				return p.Value;
			}
		}
		return null;
	}

	private static string? Str(JsonElement e, string name)
	{
		JsonElement? v = Prop(e, name);
		return v is { ValueKind: JsonValueKind.String } s ? s.GetString()
			: v is { ValueKind: JsonValueKind.Number } n ? n.GetRawText()
			: null;
	}

	private static IReadOnlyList<string> List(JsonElement e, string name)
	{
		JsonElement? v = Prop(e, name);
		if (v is not { ValueKind: JsonValueKind.Array } a)
		{
			return [];
		}
		return a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
	}
}

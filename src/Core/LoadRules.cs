using System.Text.RegularExpressions;

namespace TetzModManager;

/// <summary>
/// Which installed mods the game will load, and why not the others. Follows the game's own selection (0.6.3): the enabled
/// entries in Preferences.json and everything they require, without the mods whose requirements aren't met or that are
/// incompatible with a loaded one. The game keeps no entry for a mod another loaded mod requires.
/// </summary>
public static partial class LoadRules
{
	private sealed record Pack(string Id, string Name, string Version, string Scope, IReadOnlyList<string> Required, IReadOnlyList<string> Optional,
		IReadOnlyList<string> Incompatible, bool HasAssemblies);

	private sealed class Selection
	{
		public required List<string> Loaded { get; init; }
		public Dictionary<string, List<string>> Problems { get; } = [];
		public Dictionary<string, List<string>> RequiredBy { get; } = [];
	}

	[GeneratedRegex(@"^[a-z][a-z0-9.-]*/[a-z][a-z0-9.-]*$")]
	private static partial Regex IdentifierPattern();

	[GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)([a-z]?)$")]
	private static partial Regex VersionPattern();

	/// <summary>Fills in each mod's <see cref="InstalledMod.Loads"/>, what requires it, and the reason it isn't loaded.</summary>
	public static void Apply(List<InstalledMod> mods, Preferences prefs, string? modDevDir, string? gameVersion)
	{
		// What the game finds: the first copy of each mod in Mods (in folder order), a copy in ModDev instead of it.
		Dictionary<string, Pack> packs = new(StringComparer.Ordinal);
		Dictionary<string, InstalledMod> used = new(StringComparer.Ordinal);
		foreach (InstalledMod m in mods)
		{
			if (m.Manifest.Identifier is not string id)
			{
				if (m.Manifest.UmmAssembly != null)
				{
					m.NotLoaded = "Unity Mod Manager mod: Railroader 0.6 doesn't load it";
				}
			}
			else if (ManifestProblem(m.Manifest) is string problem)
			{
				m.NotLoaded = "Won't load: " + problem;
				m.CannotLoad = true;
			}
			else if (used.TryGetValue(id, out InstalledMod? first))
			{
				m.NotLoaded = $"Won't load: the game uses the copy in \"{first.FolderName}\"";
				m.CannotLoad = true;
			}
			else
			{
				used[id] = m;
				packs[id] = ToPack(m.Manifest);
			}
		}
		if (modDevDir != null && Directory.Exists(modDevDir))
		{
			foreach (string dir in Directory.EnumerateDirectories(modDevDir))
			{
				if (ModManifest.ReadFolder(dir) is { Identifier: string id } dev && ManifestProblem(dev) == null)
				{
					packs[id] = ToPack(dev);
				}
			}
		}

		bool dllMods = prefs.AllowDllMods;
		List<string> roots = packs.Keys.Where(id => prefs.Enabled(id) == true).ToList();
		Selection now = Select(packs, roots, dllMods, gameVersion);
		HashSet<string> loaded = new(now.Loaded, StringComparer.Ordinal);
		foreach ((string id, InstalledMod m) in used)
		{
			if (loaded.Contains(id))
			{
				m.Loads = true;
				m.RequiredBy.AddRange(now.RequiredBy.GetValueOrDefault(id, []).Where(loaded.Contains).Select(r => packs[r].Name));
				continue;
			}
			if (m.Enabled == true)
			{
				m.CannotLoad = true;
				m.NotLoaded = "Won't load: " + string.Join("; ", now.Problems.GetValueOrDefault(id, ["a mod it needs can't load"]));
				continue;
			}
			// Off: what would happen if it were on.
			List<string> blockers = Select(packs, [.. roots, id], dllMods, gameVersion).Problems.GetValueOrDefault(id, []);
			m.CannotLoad = blockers.Count > 0;
			if (m.Enabled == false)
			{
				m.OffByChoice = blockers.Count == 0;
				m.NotLoaded = "Disabled in Preferences.json" + (blockers.Count > 0 ? "; it couldn't load anyway: " + string.Join("; ", blockers) : "");
			}
			else if (blockers.Count > 0)
			{
				m.NotLoaded = "Not enabled, and can't load: " + string.Join("; ", blockers);
			}
			else
			{
				List<string> users = packs.Values.Where(p => p.Required.Any(d => IdOf(d) == id)).Select(p => p.Name).ToList();
				m.NotLoaded = users.Count > 0
					? $"Not enabled: not in Preferences.json, and {Names(users)} (which {(users.Count == 1 ? "requires" : "require")} it) {(users.Count == 1 ? "isn't" : "aren't")} loaded"
					: "Not enabled: not in Preferences.json, and no mod requires it";
			}
		}
	}

	/// <summary>"A", "A and B", "A, B and 3 more".</summary>
	public static string Names(IReadOnlyList<string> names) => names.Count switch
	{
		0 => "",
		1 => names[0],
		2 => $"{names[0]} and {names[1]}",
		3 => $"{names[0]}, {names[1]} and {names[2]}",
		_ => $"{names[0]}, {names[1]} and {names.Count - 2} more",
	};

	private static Pack ToPack(ModManifest m) =>
		new(m.Identifier!, m.Name, m.Version, Scope(m), m.Required, m.Optional, m.Incompatible, m.HasAssemblies);

	private static string Scope(ModManifest m) => (m.Scope ?? "").Trim().ToLowerInvariant() is string s && s != "unscoped" ? s : "";

	private static string? ManifestProblem(ModManifest m) =>
		!IdentifierPattern().IsMatch(m.Identifier!) ? $"the game doesn't accept the identifier \"{m.Identifier}\""
		: Scope(m) is "" or "optional" or "client" ? null
		: $"its Info.json has an unknown scope \"{m.Scope}\"";

	private static Selection Select(Dictionary<string, Pack> packs, List<string> roots, bool dllMods, string? gameVersion)
	{
		List<string> on = roots.Distinct(StringComparer.Ordinal).ToList();
		HashSet<string> set = new(on, StringComparer.Ordinal);
		Selection s = new() { Loaded = on };
		for (int i = 0; i < on.Count; i++)
		{
			foreach (string id in packs[on[i]].Required.Select(IdOf).Where(d => d != on[i]))
			{
				if (!s.RequiredBy.TryGetValue(id, out List<string>? by))
				{
					s.RequiredBy[id] = by = [];
				}
				if (!by.Contains(on[i]))
				{
					by.Add(on[i]);
				}
				if (packs.ContainsKey(id) && set.Add(id))
				{
					on.Add(id);
				}
			}
		}

		HashSet<string> rootSet = new(roots, StringComparer.Ordinal);
		void Drop(string id, IEnumerable<string> why)
		{
			s.Problems[id] = why.ToList();
			set.Remove(id);
		}
		void Prune()
		{
			bool changed;
			do
			{
				changed = false;
				foreach (string id in on.Where(set.Contains).ToList())
				{
					string? missing = packs[id].Required.Select(IdOf).FirstOrDefault(d => packs.ContainsKey(d) && !set.Contains(d));
					if (missing != null)
					{
						Drop(id, [$"needs {packs[missing].Name}, which can't load" + (s.Problems.TryGetValue(missing, out List<string>? p) ? $" ({p[0]})" : "")]);
						changed = true;
					}
				}
				HashSet<string> reached = new(on.Where(id => set.Contains(id) && rootSet.Contains(id)), StringComparer.Ordinal);
				Queue<string> queue = new(reached);
				while (queue.Count > 0)
				{
					foreach (string d in packs[queue.Dequeue()].Required.Select(IdOf).Where(set.Contains))
					{
						if (reached.Add(d))
						{
							queue.Enqueue(d);
						}
					}
				}
				changed |= set.RemoveWhere(id => !reached.Contains(id)) > 0;
			}
			while (changed);
		}

		foreach (string id in on)
		{
			List<string> problems = RequirementProblems(packs, packs[id], dllMods, gameVersion).ToList();
			if (problems.Count > 0)
			{
				Drop(id, problems);
			}
		}
		Prune();
		foreach (string id in InCycles(packs, set))
		{
			Drop(id, ["it and mods it requires require each other"]);
		}
		foreach (string id in on.Where(set.Contains).ToList())
		{
			List<string> conflicts = Conflicts(packs, packs[id], set, gameVersion).ToList();
			if (conflicts.Count > 0)
			{
				Drop(id, conflicts);
			}
		}
		Prune();
		on.RemoveAll(id => !set.Contains(id));
		return s;
	}

	private static IEnumerable<string> RequirementProblems(Dictionary<string, Pack> packs, Pack pack, bool dllMods, string? gameVersion)
	{
		if (!dllMods && pack.HasAssemblies)
		{
			yield return "it's a DLL mod, and DLL mods are off in the game's preferences";
		}
		foreach (string dep in pack.Required)
		{
			string id = IdOf(dep);
			if (!ParseConstraints(dep, out List<(string Op, int Version, string Text)> constraints))
			{
				yield return $"its requirement \"{dep}\" can't be read";
				continue;
			}
			if (id == "rr/game")
			{
				if (ParseVersion(gameVersion) is int game && !constraints.All(c => Satisfies(game, c.Op, c.Version)))
				{
					yield return $"needs Railroader {string.Join(" ", constraints.Select(c => c.Text))} ({gameVersion} installed)";
				}
				continue;
			}
			if (!packs.TryGetValue(id, out Pack? d))
			{
				if (!id.StartsWith("rr/", StringComparison.Ordinal))
				{
					yield return $"needs {id}, which isn't installed";
				}
				continue;
			}
			if (ParseVersion(d.Version) is int version && !constraints.All(c => Satisfies(version, c.Op, c.Version)))
			{
				yield return $"needs {d.Name} {string.Join(" ", constraints.Select(c => c.Text))} ({d.Version} installed)";
			}
			else if (pack.Scope != "client" && d.Scope == "client")
			{
				yield return $"needs {d.Name}, a client-only mod, which the game doesn't allow";
			}
			else if (pack.Scope == "" && d.Scope == "optional")
			{
				yield return $"needs {d.Name}, an optional mod, which the game doesn't allow";
			}
		}
	}

	private static IEnumerable<string> Conflicts(Dictionary<string, Pack> packs, Pack pack, HashSet<string> on, string? gameVersion)
	{
		foreach (string dep in pack.Incompatible)
		{
			string id = IdOf(dep);
			if (!ParseConstraints(dep, out List<(string Op, int Version, string Text)> constraints))
			{
				yield return $"its incompatibility \"{dep}\" can't be read";
				continue;
			}
			bool game = id == "rr/game";
			if (game ? gameVersion == null : !on.Contains(id))
			{
				continue;
			}
			string? installed = game ? gameVersion : packs[id].Version;
			if (ParseVersion(installed) is int v && !constraints.All(c => Satisfies(v, c.Op, c.Version)))
			{
				continue;
			}
			yield return game ? $"it's incompatible with Railroader {installed}" : $"it's incompatible with {packs[id].Name}, which is enabled";
		}
	}

	private static List<string> InCycles(Dictionary<string, Pack> packs, HashSet<string> on)
	{
		IEnumerable<string> Next(string id) => packs[id].Required.Concat(packs[id].Optional).Select(IdOf).Where(d => d != id && on.Contains(d));
		// Mods that require nothing loaded, then those requiring only such mods, can't be in a cycle.
		HashSet<string> open = new(on, StringComparer.Ordinal);
		while (open.RemoveWhere(id => !Next(id).Any(open.Contains)) > 0)
		{
		}
		return open.Where(start =>
		{
			HashSet<string> seen = new(StringComparer.Ordinal);
			Queue<string> queue = new([start]);
			while (queue.Count > 0)
			{
				foreach (string n in Next(queue.Dequeue()).Where(open.Contains))
				{
					if (n == start)
					{
						return true;
					}
					if (seen.Add(n))
					{
						queue.Enqueue(n);
					}
				}
			}
			return false;
		}).ToList();
	}

	/// <summary>A dependency's identifier as the game reads it: up to the first space.</summary>
	private static string IdOf(string dep) => dep.Split(' ')[0];

	private static bool ParseConstraints(string dep, out List<(string Op, int Version, string Text)> constraints)
	{
		constraints = [];
		string[] ops = [">=", "<=", "==", ">", "<", "~", "^"];
		string[] parts = dep.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		for (int i = 1; i < parts.Length; i++)
		{
			string text = parts[i];
			if (ops.Contains(text) && i + 1 < parts.Length)
			{
				text += parts[++i];
			}
			string op = ops.FirstOrDefault(o => text.StartsWith(o, StringComparison.Ordinal)) ?? "";
			if (ParseVersion(text[op.Length..]) is not int version)
			{
				return false;
			}
			constraints.Add((op, version, text));
		}
		return parts.Length > 0;
	}

	/// <summary>The game's version number: major.minor.patch with an optional letter, minor and patch below 100.</summary>
	private static int? ParseVersion(string? text)
	{
		Match m = VersionPattern().Match(text ?? "");
		if (!m.Success || !int.TryParse(m.Groups[1].Value, out int major) || !int.TryParse(m.Groups[2].Value, out int minor)
			|| !int.TryParse(m.Groups[3].Value, out int patch) || major > 2146 || minor > 99 || patch > 99)
		{
			return null;
		}
		return major * 1000000 + minor * 10000 + patch * 100 + (m.Groups[4].Length > 0 ? m.Groups[4].Value[0] - 'a' + 1 : 0);
	}

	private static bool Satisfies(int version, string op, int wanted)
	{
		int nextMinor = (wanted / 10000 + 1) * 10000, nextMajor = (wanted / 1000000 + 1) * 1000000;
		return op switch
		{
			"<" => version < wanted,
			"<=" => version <= wanted,
			">" => version > wanted,
			">=" => version >= wanted,
			"~" => version >= wanted && version < nextMinor,
			"^" => version >= wanted && version < (wanted < 1000000 ? nextMinor : nextMajor),
			_ => version == wanted,
		};
	}
}

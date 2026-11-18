using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TetzModManager;

public sealed record NexusFile(int FileId, string Name, string Version, string Category, string FileName, long Uploaded, bool Primary);

public sealed record NexusFiles(List<NexusFile> Files, Dictionary<int, int> Updates);

/// <summary>An nxm:// link from the website's "Mod Manager Download" button.</summary>
public sealed partial record NxmLink(string Game, int ModId, int FileId, string? Key, string? Expires)
{
	[GeneratedRegex(@"^nxm://(?<game>[^/]+)/mods/(?<mod>\d+)/files/(?<file>\d+)", RegexOptions.IgnoreCase)]
	private static partial Regex Pattern();

	public static NxmLink? Parse(string url)
	{
		Match m = Pattern().Match(url.Trim());
		if (!m.Success)
		{
			return null;
		}
		string? Query(string name)
		{
			Match q = Regex.Match(url, $@"[?&]{name}=([^&]+)");
			return q.Success ? Uri.UnescapeDataString(q.Groups[1].Value) : null;
		}
		return new NxmLink(m.Groups["game"].Value.ToLowerInvariant(), int.Parse(m.Groups["mod"].Value), int.Parse(m.Groups["file"].Value), Query("key"), Query("expires"));
	}
}

/// <summary>The Nexus Mods v1 API for one game. The key goes only to api.nexusmods.com and is never logged.</summary>
public sealed class NexusClient : IDisposable
{
	public const string GameDomain = "railroader";
	private readonly HttpClient _http;

	public NexusClient(string apiKey)
	{
		_http = new HttpClient { BaseAddress = new Uri("https://api.nexusmods.com/v1/"), Timeout = TimeSpan.FromMinutes(10) };
		_http.DefaultRequestHeaders.Add("apikey", apiKey);
		_http.DefaultRequestHeaders.Add("Application-Name", "RailroaderModManager");
		_http.DefaultRequestHeaders.Add("Application-Version", "0.1.0");
		_http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RailroaderModManager", "0.1.0"));
		_http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
	}

	public void Dispose() => _http.Dispose();

	private async Task<JsonElement> Get(string path, CancellationToken ct)
	{
		using HttpResponseMessage res = await _http.GetAsync(path, ct);
		string body = await res.Content.ReadAsStringAsync(ct);
		if (!res.IsSuccessStatusCode)
		{
			string message = body;
			try
			{
				using JsonDocument d = JsonDocument.Parse(body);
				if (d.RootElement.TryGetProperty("message", out JsonElement m))
				{
					message = m.GetString() ?? body;
				}
			}
			catch (JsonException)
			{
			}
			throw new HttpRequestException($"Nexus: {(int)res.StatusCode} {message}");
		}
		using JsonDocument doc = JsonDocument.Parse(body);
		return doc.RootElement.Clone();
	}

	public async Task<NexusFiles> Files(int modId, CancellationToken ct = default)
	{
		JsonElement root = await Get($"games/{GameDomain}/mods/{modId}/files.json", ct);
		List<NexusFile> files = root.GetProperty("files").EnumerateArray().Select(ToFile).ToList();
		Dictionary<int, int> updates = [];
		if (root.TryGetProperty("file_updates", out JsonElement u))
		{
			foreach (JsonElement x in u.EnumerateArray())
			{
				updates[x.GetProperty("old_file_id").GetInt32()] = x.GetProperty("new_file_id").GetInt32();
			}
		}
		return new NexusFiles(files, updates);
	}

	public async Task<NexusFile> File(int modId, int fileId, CancellationToken ct = default) =>
		ToFile(await Get($"games/{GameDomain}/mods/{modId}/files/{fileId}.json", ct));

	public async Task<(string Name, string Version)> Mod(int modId, CancellationToken ct = default)
	{
		JsonElement m = await Get($"games/{GameDomain}/mods/{modId}.json", ct);
		return (Str(m, "name"), Str(m, "version"));
	}

	/// <summary>The download URL for a file: a Premium key needs no nxm key; otherwise the link's key and expiry.</summary>
	public async Task<string> DownloadLink(int modId, int fileId, string? key, string? expires, CancellationToken ct = default)
	{
		string q = key != null && expires != null ? $"?key={Uri.EscapeDataString(key)}&expires={Uri.EscapeDataString(expires)}" : "";
		JsonElement links = await Get($"games/{GameDomain}/mods/{modId}/files/{fileId}/download_link.json{q}", ct);
		return links.EnumerateArray().First().GetProperty("URI").GetString()!;
	}

	public async Task Download(string url, string dest, IProgress<double>? progress, CancellationToken ct = default)
	{
		// The CDN link is signed; the API key isn't sent there.
		using HttpClient cdn = new() { Timeout = TimeSpan.FromMinutes(30) };
		cdn.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RailroaderModManager", "0.1.0"));
		using HttpResponseMessage res = await cdn.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
		res.EnsureSuccessStatusCode();
		long? total = res.Content.Headers.ContentLength;
		Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
		await using Stream src = await res.Content.ReadAsStreamAsync(ct);
		await using FileStream dst = System.IO.File.Create(dest);
		byte[] buffer = new byte[1 << 16];
		long done = 0;
		int n;
		while ((n = await src.ReadAsync(buffer, ct)) > 0)
		{
			await dst.WriteAsync(buffer.AsMemory(0, n), ct);
			done += n;
			if (total > 0)
			{
				progress?.Report((double)done / total.Value);
			}
		}
	}

	private static NexusFile ToFile(JsonElement f) => new(
		f.GetProperty("file_id").GetInt32(),
		Str(f, "name"),
		Str(f, "version"),
		Str(f, "category_name"),
		Str(f, "file_name"),
		f.TryGetProperty("uploaded_timestamp", out JsonElement t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0,
		f.TryGetProperty("is_primary", out JsonElement p) && p.ValueKind == JsonValueKind.True);

	private static string Str(JsonElement e, string name) =>
		e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>Update checks: follows Nexus's file update chain from the installed file to the newest one.</summary>
public static class Updates
{
	public static async Task<int> Check(NexusClient nexus, StateStore state, IReadOnlyList<InstalledPackage> packages, Action<string> log, CancellationToken ct = default)
	{
		int found = 0;
		foreach (IGrouping<int, InstalledPackage> g in packages.Where(p => p.Record?.NexusModId != null && (p.Members.Count > 0 || p.Record.IsAddOn))
			.GroupBy(p => p.Record!.NexusModId!.Value))
		{
			NexusFiles files;
			try
			{
				files = await nexus.Files(g.Key, ct);
			}
			catch (HttpRequestException e)
			{
				log($"Nexus {g.Key}: {e.Message}");
				continue;
			}
			foreach (InstalledPackage p in g)
			{
				PackageRecord r = p.Record!;
				// The Nexus version known from the install (or Vortex's download) first: authors often leave Info.json at 1.0.0.
				NexusFile? current = (r.InstalledNexusVersion != null ? Resolve(files, r, r.InstalledNexusVersion) : null) ?? Resolve(files, r, p.Version);
				if (current != null)
				{
					r.NexusFileId = current.FileId;
					r.NexusFileName ??= current.FileName;
					r.InstalledNexusVersion = current.Version;
					NexusFile latest = Latest(files, current);
					r.LatestFileId = latest.FileId;
					r.LatestVersion = latest.Version;
				}
				else
				{
					// The installed version isn't on the page (a local build, or an old file removed): compare with the page's main file.
					NexusFile? main = files.Files.Where(f => f.Category == "MAIN").OrderByDescending(f => f.Primary).ThenByDescending(f => f.Uploaded).FirstOrDefault();
					r.LatestFileId = main?.FileId;
					r.LatestVersion = main?.Version;
				}
				r.CheckedAt = DateTime.Now;
				if (p.UpdateAvailable)
				{
					found++;
					log($"Update: {p.Name} {p.InstalledVersion} -> {r.LatestVersion}");
				}
			}
		}
		state.Save();
		return found;
	}

	/// <summary>The Nexus file the installed version came from: the recorded one, else the newest file with the same version.</summary>
	private static NexusFile? Resolve(NexusFiles files, PackageRecord r, string installedVersion)
	{
		if (r.NexusFileId is int id && files.Files.FirstOrDefault(f => f.FileId == id) is NexusFile known && Versions.Same(known.Version, installedVersion))
		{
			return known;
		}
		return files.Files.Where(f => Versions.Same(f.Version, installedVersion))
			.OrderByDescending(f => r.NexusFileName != null && string.Equals(f.FileName, r.NexusFileName, StringComparison.OrdinalIgnoreCase))
			.ThenByDescending(f => f.Category == "MAIN")
			.ThenByDescending(f => f.Uploaded)
			.FirstOrDefault();
	}

	private static NexusFile Latest(NexusFiles files, NexusFile from)
	{
		NexusFile at = from;
		HashSet<int> seen = [from.FileId];
		// Authors sometimes link a file "updated" to an older one (RTM 6.2 -> 6.1): never step back to a lower version.
		while (files.Updates.TryGetValue(at.FileId, out int next) && seen.Add(next) && files.Files.FirstOrDefault(f => f.FileId == next) is NexusFile n
			&& Versions.Compare(n.Version, at.Version) >= 0)
		{
			at = n;
		}
		return at;
	}
}

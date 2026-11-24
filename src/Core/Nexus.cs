using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TetzModManager;

/// <summary>A file on a Nexus page. v2 gives no archive name (FileName is empty) but a group: the versions of one download.</summary>
public sealed record NexusFile(int FileId, string Name, string Version, string Category, string FileName, long Uploaded, bool Primary, long? Group = null);

/// <summary>Nexus refuses requests for a while (429): asking for more only makes it last longer, so a check stops here.</summary>
public sealed class NexusLimitException(string message) : HttpRequestException(message)
{
	public static NexusLimitException New() =>
		new("Nexus is limiting requests from this computer for now (429: too many in a short time). Wait a few minutes and try again.");
}

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

/// <summary>The Nexus Mods v1 API for one game: downloads. The key goes only to api.nexusmods.com and is never logged.</summary>
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
		if (res.StatusCode == HttpStatusCode.TooManyRequests)
		{
			throw NexusLimitException.New();
		}
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

/// <summary>
/// The Nexus Mods v2 (GraphQL) API for update checks: needs no API key and reads many pages per request (v1 takes one
/// request per page, and Nexus limits requests). It can't download.
/// </summary>
public sealed class NexusPublicClient : IDisposable
{
	private const int GameId = 5982;
	private const int PagesPerRequest = 20;
	private readonly HttpClient _http;
	private readonly Dictionary<int, List<NexusFile>> _files = [];
	private readonly Dictionary<int, string> _status = [];

	public NexusPublicClient()
	{
		_http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
		_http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RailroaderModManager", "0.1.0"));
	}

	public void Dispose() => _http.Dispose();

	/// <summary>Reads all pages, several per request. Throws when Nexus doesn't answer: part of a list isn't a check.</summary>
	public async Task Prepare(IReadOnlyList<int> modIds, CancellationToken ct = default)
	{
		_files.Clear();
		_status.Clear();
		foreach (int[] ids in modIds.Distinct().Chunk(PagesPerRequest))
		{
			string query = "{ mods: legacyModsByDomain(ids: ["
				+ string.Join(", ", ids.Select(i => $"{{gameDomain: \"{NexusClient.GameDomain}\", modId: {i}}}"))
				+ $"], count: {PagesPerRequest}) {{ nodes {{ modId status }} }} "
				+ string.Join(" ", ids.Select(i => $"m{i}: modFiles(modId: {i}, gameId: {GameId}) {{ fileId name version date category primary groupId }}"))
				+ " }";
			using HttpResponseMessage res = await _http.PostAsJsonAsync("https://api.nexusmods.com/v2/graphql", new { query }, ct);
			string body = await res.Content.ReadAsStringAsync(ct);
			if (res.StatusCode == HttpStatusCode.TooManyRequests)
			{
				throw NexusLimitException.New();
			}
			if (!res.IsSuccessStatusCode)
			{
				throw new HttpRequestException($"Nexus: {(int)res.StatusCode} {res.ReasonPhrase}");
			}
			using JsonDocument doc = JsonDocument.Parse(body);
			if (doc.RootElement.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
			{
				throw new HttpRequestException("Nexus: " + (errors[0].TryGetProperty("message", out JsonElement m) ? m.GetString() : "the request failed"));
			}
			JsonElement data = doc.RootElement.GetProperty("data");
			foreach (JsonElement n in data.GetProperty("mods").GetProperty("nodes").EnumerateArray())
			{
				_status[n.GetProperty("modId").GetInt32()] = n.GetProperty("status").GetString() ?? "";
			}
			foreach (int id in ids)
			{
				_files[id] = data.GetProperty($"m{id}").EnumerateArray().Select(f => new NexusFile(
					f.GetProperty("fileId").GetInt32(),
					f.GetProperty("name").GetString() ?? "",
					f.GetProperty("version").GetString() ?? "",
					f.GetProperty("category").GetString() ?? "",
					"",
					f.GetProperty("date").GetInt64(),
					f.GetProperty("primary").ValueKind == JsonValueKind.Number && f.GetProperty("primary").GetInt32() == 1,
					f.GetProperty("groupId").ValueKind == JsonValueKind.Number ? f.GetProperty("groupId").GetInt64() : null)).ToList();
			}
		}
	}

	/// <summary>A prepared page's files. v2 lists a hidden page's files too, but they can't be downloaded.</summary>
	public List<NexusFile> Files(int modId)
	{
		string status = _status.GetValueOrDefault(modId, "not found");
		return status == "published" ? _files[modId] : throw new HttpRequestException($"mod not available: {modId} ({status})");
	}
}

/// <summary>Update checks: from the installed file to the newest one in its group (the versions of one download).</summary>
public static class Updates
{
	/// <summary>Zip times carry no time zone: a build is "newer" only by more than this.</summary>
	private static readonly TimeSpan LocalBuildSlack = TimeSpan.FromDays(1);

	public static async Task<int> Check(NexusPublicClient nexus, StateStore state, IReadOnlyList<InstalledPackage> packages, Action<string> log, CancellationToken ct = default)
	{
		int found = 0;
		List<IGrouping<int, InstalledPackage>> pages = packages.Where(p => p.Record?.NexusModId != null && (p.Members.Count > 0 || p.Record.IsAddOn))
			.GroupBy(p => p.Record!.NexusModId!.Value).ToList();
		await nexus.Prepare(pages.Select(g => g.Key).ToList(), ct);
		foreach (IGrouping<int, InstalledPackage> g in pages)
		{
			List<NexusFile> files;
			try
			{
				files = nexus.Files(g.Key);
			}
			catch (HttpRequestException e)
			{
				log($"Nexus {g.Key}: {e.Message}");
				continue;
			}
			foreach (InstalledPackage p in g)
			{
				PackageRecord r = p.Record!;
				// Which Nexus file is installed is only guessed (from the version) for manual links and local installs.
				bool guessed = r.NexusFileId == null || r.LinkSource == "manual";
				// The Nexus version known from the install (or Vortex's download) first: authors often leave Info.json at 1.0.0.
				NexusFile? current = (r.InstalledNexusVersion != null ? Resolve(files, r, r.InstalledNexusVersion) : null) ?? Resolve(files, r, p.Version);
				if (current != null)
				{
					r.NexusFileId = current.FileId;
					r.InstalledNexusVersion = current.Version;
					NexusFile latest = Latest(files, current);
					r.LatestFileId = latest.FileId;
					r.LatestVersion = latest.Version;
					// Authors renumber (1.3, then 1 for a new game version): an update is a later upload, whatever its number.
					// A re-upload under the same version isn't offered.
					r.LatestIsNewer = latest.FileId == current.FileId || Versions.Same(latest.Version, current.Version) ? false
						: latest.Uploaded > 0 && current.Uploaded > 0 ? latest.Uploaded > current.Uploaded
						: null;
				}
				else
				{
					// The installed version isn't on the page (a local build, or an old file removed): compare with the page's main file.
					NexusFile? main = files.Where(f => f.Category == "MAIN").OrderByDescending(f => f.Primary).ThenByDescending(f => f.Uploaded).FirstOrDefault();
					r.LatestFileId = main?.FileId;
					r.LatestVersion = main?.Version;
					r.LatestIsNewer = null;
				}
				// A local or test build (Freeman's test packs keep 1.0.0, which can equal an old Nexus file's version): files
				// newer than the page's newest file mean it isn't that file, and Nexus has nothing newer.
				r.NewerThanNexus = false;
				if (guessed && r.LatestFileId is int lid && lid != r.NexusFileId && files.FirstOrDefault(f => f.FileId == lid) is NexusFile lf && lf.Uploaded > 0
					&& p.NewestFile() is DateTime built && built > DateTimeOffset.FromUnixTimeSeconds(lf.Uploaded).UtcDateTime + LocalBuildSlack)
				{
					r.NewerThanNexus = true;
					r.NexusFileId = null;
					r.InstalledNexusVersion = null;
					log($"{p.Name}: the installed files ({built.ToLocalTime():yyyy-MM-dd HH:mm}) are newer than Nexus's {lf.Version}: a local build, not updated");
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
	private static NexusFile? Resolve(List<NexusFile> files, PackageRecord r, string installedVersion)
	{
		if (r.NexusFileId is int id && files.FirstOrDefault(f => f.FileId == id) is NexusFile known && Versions.Same(known.Version, installedVersion))
		{
			return known;
		}
		return files.Where(f => Versions.Same(f.Version, installedVersion))
			// v2 gives no archive names: pages of several downloads with one version (livery packs) by the download's name.
			.OrderByDescending(f => (r.NexusFileName ?? r.SourceName) is string source && SameName(Path.GetFileNameWithoutExtension(source), f.Name))
			.ThenByDescending(f => f.Category == "MAIN")
			.ThenByDescending(f => f.Uploaded)
			.FirstOrDefault();
	}

	private static bool SameName(string a, string b)
	{
		static string Bare(string s) => new(Installer.CleanName(s).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
		return Bare(a) is { Length: > 0 } x && x == Bare(b);
	}

	private static bool IsOld(NexusFile f) => f.Category is not ("MAIN" or "OPTIONAL" or "MISCELLANEOUS");

	private static NexusFile Latest(List<NexusFile> files, NexusFile from)
	{
		// A file that is still offered is only replaced by one of its own category: authors sometimes put a variant (an
		// optional file) into the main file's group.
		return files.Where(f => from.Group != null && f.Group == from.Group && f.Uploaded >= from.Uploaded && (IsOld(from) || f.Category == from.Category))
			.OrderByDescending(f => f.Uploaded).FirstOrDefault() ?? from;
	}
}

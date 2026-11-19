using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TetzModManager;

/// <summary>A release of the manager itself on GitHub.</summary>
public sealed record ManagerRelease(Version Version, string Tag, string ZipUrl, string Page, string Notes);

/// <summary>Updates of the manager itself: the newest GitHub release, downloaded and swapped in once the manager has closed.</summary>
public static class ManagerUpdate
{
	public const string Repo = "Tetz95/TetzModManager";
	private const string ExeName = "TetzModManager.exe";

	/// <summary>The running version (major.minor.build).</summary>
	public static Version Current => Trim(typeof(ManagerUpdate).Assembly.GetName().Version ?? new Version(0, 0, 0));

	private static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

	public static Version? Parse(string tag) =>
		Version.TryParse(tag.Trim().TrimStart('v', 'V'), out Version? v) ? Trim(v) : null;

	private static HttpClient Http(TimeSpan timeout)
	{
		HttpClient http = new() { Timeout = timeout };
		http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TetzModManager", Current.ToString()));
		return http;
	}

	/// <summary>The newest published release with a zip, or null when it has none.</summary>
	public static async Task<ManagerRelease?> Latest(CancellationToken ct = default)
	{
		using HttpClient http = Http(TimeSpan.FromSeconds(30));
		http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
		using JsonDocument doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct));
		JsonElement r = doc.RootElement;
		string tag = r.GetProperty("tag_name").GetString() ?? "";
		if (Parse(tag) is not Version version)
		{
			return null;
		}
		string? zip = r.GetProperty("assets").EnumerateArray()
			.Where(a => (a.GetProperty("name").GetString() ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
			.Select(a => a.GetProperty("browser_download_url").GetString())
			.FirstOrDefault();
		if (zip == null)
		{
			return null;
		}
		return new ManagerRelease(version, tag, zip, r.GetProperty("html_url").GetString() ?? "",
			r.TryGetProperty("body", out JsonElement b) ? b.GetString() ?? "" : "");
	}

	/// <summary>Downloads and extracts a release; returns the folder holding its exe.</summary>
	public static async Task<string> Download(ManagerRelease release, IProgress<double>? progress, CancellationToken ct = default)
	{
		string dir = Path.Combine(Settings.WorkDir, "manager-update");
		Files.TryDeleteDirectory(dir);
		Directory.CreateDirectory(dir);
		string zip = Path.Combine(dir, $"TetzModManager-{release.Version}.zip");
		using (HttpClient http = Http(TimeSpan.FromMinutes(10)))
		using (HttpResponseMessage resp = await http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
		{
			resp.EnsureSuccessStatusCode();
			long? total = resp.Content.Headers.ContentLength;
			await using Stream src = await resp.Content.ReadAsStreamAsync(ct);
			await using FileStream dst = File.Create(zip);
			byte[] buf = new byte[81920];
			long done = 0;
			int n;
			while ((n = await src.ReadAsync(buf, ct)) > 0)
			{
				await dst.WriteAsync(buf.AsMemory(0, n), ct);
				done += n;
				if (total > 0)
				{
					progress?.Report((double)done / total.Value);
				}
			}
		}
		string extract = Path.Combine(dir, "files");
		ZipFile.ExtractToDirectory(zip, extract);
		string? exe = Directory.EnumerateFiles(extract, ExeName, SearchOption.AllDirectories).FirstOrDefault();
		return exe != null ? Path.GetDirectoryName(exe)! : throw new InvalidDataException($"The release zip has no {ExeName}.");
	}

	/// <summary>
	/// Starts a hidden script that waits for this process to exit, copies the new files over the install folder and
	/// starts the manager again. The caller then closes the manager.
	/// </summary>
	public static void ReplaceAfterExit(string newDir, string installDir)
	{
		int pid = Environment.ProcessId;
		string dir = Path.Combine(Settings.WorkDir, "manager-update");
		string script = Path.Combine(dir, "apply.cmd");
		string log = Path.Combine(dir, "apply.log");
		string exe = Path.Combine(installDir, ExeName);
		StringBuilder s = new();
		s.AppendLine("@echo off");
		s.AppendLine(":wait");
		s.AppendLine($"tasklist /FI \"PID eq {pid}\" /NH | find \" {pid} \" >nul");
		s.AppendLine("if not errorlevel 1 (ping -n 2 127.0.0.1 >nul & goto wait)");
		s.AppendLine($"robocopy \"{newDir.TrimEnd('\\')}\" \"{installDir.TrimEnd('\\')}\" /E /R:10 /W:1 /NP /LOG:\"{log}\" >nul");
		s.AppendLine($"start \"\" \"{exe}\"");
		File.WriteAllText(script, s.ToString(), Encoding.Default);
		Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\"\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = dir });
	}

	/// <summary>The folder the running manager was started from, when it's an installed copy that can be replaced.</summary>
	public static string? InstallDir()
	{
		string dir = AppContext.BaseDirectory.TrimEnd('\\');
		return File.Exists(Path.Combine(dir, ExeName)) ? dir : null;
	}
}

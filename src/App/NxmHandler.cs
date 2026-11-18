using System.Diagnostics;
using Microsoft.Win32;

namespace TetzModManager.App;

/// <summary>
/// Windows' handler for nxm:// links (HKCU\Software\Classes\nxm). Taking it over keeps the previous command (Vortex's)
/// in the settings: links for other games are passed to it, and it can be put back.
/// </summary>
public static class NxmHandler
{
	private const string ClassKey = @"Software\Classes\nxm";
	private const string CommandKey = ClassKey + @"\shell\open\command";

	public static string ExePath => Environment.ProcessPath ?? "";

	public static string? CurrentCommand()
	{
		using RegistryKey? k = Registry.CurrentUser.OpenSubKey(CommandKey);
		return k?.GetValue("") as string;
	}

	public static bool IsOurs() => CurrentCommand()?.Contains(ExePath, StringComparison.OrdinalIgnoreCase) == true;

	public static void TakeOver(Settings settings)
	{
		string? current = CurrentCommand();
		if (current != null && !IsOurs())
		{
			settings.PreviousNxmCommand = current;
			settings.Save();
		}
		using (RegistryKey k = Registry.CurrentUser.CreateSubKey(ClassKey))
		{
			k.SetValue("", "URL:NXM Protocol");
			k.SetValue("URL Protocol", "");
		}
		using RegistryKey c = Registry.CurrentUser.CreateSubKey(CommandKey);
		c.SetValue("", $"\"{ExePath}\" \"%1\"");
	}

	public static void GiveBack(Settings settings)
	{
		if (string.IsNullOrEmpty(settings.PreviousNxmCommand))
		{
			throw new InvalidOperationException("No earlier nxm handler is recorded.");
		}
		using RegistryKey c = Registry.CurrentUser.CreateSubKey(CommandKey);
		c.SetValue("", settings.PreviousNxmCommand);
	}

	/// <summary>Runs the previous handler's command for a link this tool doesn't handle (another game's).</summary>
	public static void Forward(Settings settings, string url)
	{
		string cmd = settings.PreviousNxmCommand;
		if (string.IsNullOrEmpty(cmd))
		{
			throw new InvalidOperationException("This link is for another game, and no other nxm handler is recorded.");
		}
		string exe, args;
		if (cmd.StartsWith('"'))
		{
			int end = cmd.IndexOf('"', 1);
			exe = cmd[1..end];
			args = cmd[(end + 1)..].Trim();
		}
		else
		{
			int sp = cmd.IndexOf(' ');
			exe = sp < 0 ? cmd : cmd[..sp];
			args = sp < 0 ? "" : cmd[(sp + 1)..];
		}
		ProcessStartInfo psi = new(exe, args.Replace("%1", url)) { UseShellExecute = false };
		// Started from VS Code this is set, and it makes Vortex (Electron) run as plain Node.
		psi.Environment.Remove("ELECTRON_RUN_AS_NODE");
		Process.Start(psi);
	}
}

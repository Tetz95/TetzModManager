using System.IO;
using System.IO.Pipes;
using System.Windows;

namespace TetzModManager.App;

/// <summary>
/// One window at a time: a second start (an nxm link clicked on the website) hands its arguments to the running window
/// over a named pipe and exits.
/// </summary>
public partial class App : Application
{
	private const string PipeName = "TetzModManager.Links";
	private Mutex? _single;

	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		if (Environment.GetEnvironmentVariable("TETZMM_SNAPSHOT") != null)
		{
			MainWindow = new MainWindow(); // a test render, beside a running manager
			MainWindow.Show();
			return;
		}
		_single = new Mutex(true, @"Local\TetzModManager.Single", out bool first);
		if (!first)
		{
			try
			{
				using NamedPipeClientStream client = new(".", PipeName, PipeDirection.Out);
				client.Connect(5000);
				using StreamWriter w = new(client);
				foreach (string arg in e.Args)
				{
					w.WriteLine(arg);
				}
			}
			catch (Exception ex) when (ex is IOException or TimeoutException)
			{
				MessageBox.Show("Tetz's Mod Manager is already open but didn't answer: " + ex.Message, "Tetz's Mod Manager");
			}
			Shutdown();
			return;
		}
		MainWindow window = new();
		MainWindow = window;
		window.Show();
		Task.Run(() => Listen(window));
		foreach (string arg in e.Args)
		{
			window.HandleArgument(arg);
		}
	}

	private static async Task Listen(MainWindow window)
	{
		while (true)
		{
			try
			{
				await using NamedPipeServerStream server = new(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
				await server.WaitForConnectionAsync();
				using StreamReader r = new(server);
				string? line;
				while ((line = await r.ReadLineAsync()) != null)
				{
					string arg = line;
					window.Dispatcher.Invoke(() =>
					{
						window.Activate();
						window.HandleArgument(arg);
					});
				}
			}
			catch (IOException)
			{
			}
		}
	}

	protected override void OnExit(ExitEventArgs e)
	{
		_single?.Dispose();
		base.OnExit(e);
	}
}

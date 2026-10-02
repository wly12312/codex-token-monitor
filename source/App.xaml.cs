using System.IO;
using System.Text.Json;
using System.Windows;

namespace CodexTokenMonitor;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var paths = StartupOptions.Apply(e.Args);
            var diagnosticPath = StartupOptions.Value(e.Args, "--diagnose-paths");
            if (diagnosticPath is not null)
            {
                var modelLog = Environment.GetEnvironmentVariable("CODEX_TOKEN_MONITOR_MODEL_LOG_PATH");
                await WriteOutputAsync(diagnosticPath, new
                {
                    ApplicationDirectory = AppContext.BaseDirectory,
                    paths.CodexHome,
                    paths.SessionRoot,
                    paths.WatchRoot,
                    paths.SettingsPath,
                    ModelLogPath = string.IsNullOrWhiteSpace(modelLog) ? null : MonitorPaths.ResolvePath(modelLog),
                    SessionDirectoryExists = Directory.Exists(paths.SessionRoot),
                    IndexExists = File.Exists(Path.Combine(paths.WatchRoot, "session_index.jsonl")),
                    Note = "Only paths and file existence were checked. No session or authentication contents were read."
                });
                Shutdown(0);
                return;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Codex Token Monitor — path settings", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        var renderArg = e.Args.FirstOrDefault(x => x.StartsWith("--render-previews=", StringComparison.OrdinalIgnoreCase));
        var requestedSessionId = e.Args.FirstOrDefault(x => x.StartsWith("--session-id=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1].Trim('"');
        var requestedScopeKey = e.Args.FirstOrDefault(x => x.StartsWith("--scope=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1].Trim('"');
        if (renderArg is not null)
        {
            try
            {
                var outputDirectory = MonitorPaths.ResolvePath(StartupOptions.Value(e.Args, "--render-previews")!);
                var window = new MainWindow(startTimer: false);
                await window.RenderPreviewsAsync(outputDirectory, requestedSessionId, requestedScopeKey);
                window.Close();
                Shutdown(0);
            }
            catch { Shutdown(1); }
            return;
        }

        var smokeArg = e.Args.FirstOrDefault(x => x.StartsWith("--smoke-test=", StringComparison.OrdinalIgnoreCase));
        if (smokeArg is not null)
        {
            try
            {
                var outputPath = smokeArg[(smokeArg.IndexOf('=') + 1)..].Trim('"');
                var parser = new SessionParser();
                var path = parser.GetLatestUserSession();
                if (path is null) throw new InvalidOperationException("No user Codex session found.");
                var data = parser.Parse(path);
                var payload = new
                {
                    data.Path,
                    data.SessionId,
                    data.CurrentModel,
                    Turns = data.Rows.Count,
                    data.Totals.Input,
                    data.Totals.Cached,
                    data.Totals.Output,
                    data.TotalCredits,
                    data.TotalUsd,
                    data.FiveHour,
                    data.Weekly,
                    Recent = parser.GetRecentUserSessions(5).Select(f => new { f.FullName, f.LastWriteTime }),
                    UiConstructed = false
                };
                await WriteOutputAsync(outputPath, payload);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                var outputPath = smokeArg[(smokeArg.IndexOf('=') + 1)..].Trim('"');
                await TryWriteErrorAsync(outputPath, ex);
                Shutdown(1);
            }
            return;
        }

        var uiSmokeArg = e.Args.FirstOrDefault(x => x.StartsWith("--ui-smoke-test=", StringComparison.OrdinalIgnoreCase));
        if (uiSmokeArg is not null)
        {
            var outputPath = uiSmokeArg[(uiSmokeArg.IndexOf('=') + 1)..].Trim('"');
            try
            {
                var window = new MainWindow(startTimer: false);
                var result = await window.RunSmokeTestAsync(requestedSessionId);
                await WriteOutputAsync(outputPath, result);
                window.Close();
                Shutdown(0);
            }
            catch (Exception ex)
            {
                await TryWriteErrorAsync(outputPath, ex);
                Shutdown(1);
            }
            return;
        }

        var mainWindow = new MainWindow(startTimer: true);
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private static async Task WriteOutputAsync(string path, object payload)
    {
        var absolute = MonitorPaths.ResolvePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllTextAsync(absolute, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task TryWriteErrorAsync(string path, Exception error)
    {
        try { await WriteOutputAsync(path, new { Error = error.ToString() }); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { }
    }
}

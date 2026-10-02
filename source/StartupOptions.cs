using System.IO;

namespace CodexTokenMonitor;

public static class StartupOptions
{
    // Overrides apply only to this process; Codex's configuration is never changed.
    public static MonitorPaths Apply(string[] args)
    {
        var home = Value(args, "--codex-home");
        var sessions = Value(args, "--sessions");
        var settings = Value(args, "--settings");
        var modelLog = Value(args, "--model-log");
        if (home is not null)
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", MonitorPaths.ResolvePath(home));
            if (sessions is null) Environment.SetEnvironmentVariable("CODEX_TOKEN_MONITOR_SESSION_ROOT", null);
        }
        if (sessions is not null)
            Environment.SetEnvironmentVariable("CODEX_TOKEN_MONITOR_SESSION_ROOT", MonitorPaths.ResolvePath(sessions));
        if (settings is not null)
            Environment.SetEnvironmentVariable("CODEX_TOKEN_MONITOR_SETTINGS_PATH", MonitorPaths.ResolvePath(settings));
        else if (args.Contains("--portable", StringComparer.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("CODEX_TOKEN_MONITOR_SETTINGS_PATH", Path.Combine(AppContext.BaseDirectory, "data", "settings.json"));
        if (modelLog is not null)
            Environment.SetEnvironmentVariable("CODEX_TOKEN_MONITOR_MODEL_LOG_PATH", MonitorPaths.ResolvePath(modelLog));
        return new MonitorPaths();
    }

    public static string? Value(string[] args, string name)
    {
        var argument = args.LastOrDefault(x => x.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        if (argument is null) return null;
        var value = argument[(name.Length + 1)..].Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} requires a non-empty path.");
        return value;
    }
}

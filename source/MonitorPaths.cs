using System.IO;
using System.Text.RegularExpressions;

namespace CodexTokenMonitor;

// Resolve paths once per reader. Never create or modify the Codex data directories.
public sealed class MonitorPaths
{
    public string CodexHome { get; }
    public string SessionRoot { get; }
    public string WatchRoot { get; }
    public string SettingsPath { get; }

    public MonitorPaths(string? baseDirectory = null)
        : this(baseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) { }

    internal MonitorPaths(string? baseDirectory, string userProfile, string localApplicationData)
    {
        var applicationDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory, AppContext.BaseDirectory);
        if (string.IsNullOrWhiteSpace(userProfile)) throw new InvalidOperationException("The current user's home directory is unavailable.");
        if (string.IsNullOrWhiteSpace(localApplicationData)) throw new InvalidOperationException("The current user's local application data directory is unavailable.");
        string Resolve(string path) => ResolvePathCore(path, applicationDirectory, userProfile);
        var homeOverride = EnvironmentPath("CODEX_HOME");
        CodexHome = homeOverride is null ? Path.GetFullPath(Path.Combine(userProfile, ".codex"), applicationDirectory) : Resolve(homeOverride);
        var sessionOverride = EnvironmentPath("CODEX_TOKEN_MONITOR_SESSION_ROOT");
        SessionRoot = sessionOverride is null ? Path.Combine(CodexHome, "sessions") : Resolve(sessionOverride);
        // An independent sessions override keeps its own adjacent index and log database.
        WatchRoot = Directory.GetParent(Path.TrimEndingDirectorySeparator(SessionRoot))?.FullName ?? SessionRoot;
        var settingsOverride = EnvironmentPath("CODEX_TOKEN_MONITOR_SETTINGS_PATH");
        SettingsPath = settingsOverride is null
            ? Path.GetFullPath(Path.Combine(localApplicationData, "CodexTokenMonitor", "settings.json"), applicationDirectory)
            : Resolve(settingsOverride);
    }

    public static string ResolvePath(string value, string? baseDirectory = null)
    {
        var applicationDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory, AppContext.BaseDirectory);
        return ResolvePathCore(value, applicationDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    internal static string? EnvironmentPath(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string ResolvePathCore(string value, string baseDirectory, string userProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        // Also support shell-style variables in configuration supplied outside PowerShell.
        expanded = Regex.Replace(expanded, @"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}|\$(?<name>[A-Za-z_][A-Za-z0-9_]*)",
            match => Environment.GetEnvironmentVariable(match.Groups["name"].Value) ?? match.Value);
        if (expanded == "~" || expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(userProfile)) throw new InvalidOperationException("The current user's home directory is unavailable.");
            expanded = expanded.Length == 1 ? userProfile : Path.Combine(userProfile, expanded[2..]);
        }
        return Path.GetFullPath(expanded, baseDirectory);
    }
}

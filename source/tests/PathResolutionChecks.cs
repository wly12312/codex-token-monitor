using System.Text.Json;
using CodexTokenMonitor;

public static class PathResolutionChecks
{
    public static void Run()
    {
        const string variable = "CODEX_TOKEN_MONITOR_PATH_TEST";
        var names = new[] { "CODEX_HOME", "CODEX_TOKEN_MONITOR_SESSION_ROOT", "CODEX_TOKEN_MONITOR_SETTINGS_PATH", "CODEX_TOKEN_MONITOR_MODEL_LOG_PATH", variable };
        var saved = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var originalDirectory = Environment.CurrentDirectory;
        var fixtureRoot = Path.Combine(Path.GetDirectoryName(saved["CODEX_TOKEN_MONITOR_SESSION_ROOT"]!)!, "path-fixtures");
        var application = Path.Combine(fixtureRoot, "app with spaces");
        var otherDirectory = Path.Combine(fixtureRoot, "unrelated working directory");
        var userOne = Path.Combine(fixtureRoot, "users", "Alice");
        var userTwo = Path.Combine(fixtureRoot, "users", "第二位用户");
        var localOne = Path.Combine(userOne, "local-data");
        var localTwo = Path.Combine(userTwo, "local-data");
        var bareSettings = Path.Combine(AppContext.BaseDirectory, "path-check-" + Guid.NewGuid().ToString("N") + ".json");
        var readOnlyFiles = new List<string>();
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, null);
            Directory.CreateDirectory(application);
            Directory.CreateDirectory(otherDirectory);
            Environment.CurrentDirectory = otherDirectory;
            var first = new MonitorPaths(application, userOne, localOne);
            var second = new MonitorPaths(application, userTwo, localTwo);
            Equal(first.CodexHome, Path.Combine(userOne, ".codex"), "default first user home");
            Equal(second.CodexHome, Path.Combine(userTwo, ".codex"), "default second user home");
            Equal(first.SessionRoot, Path.Combine(first.CodexHome, "sessions"), "default sessions");
            Equal(first.WatchRoot, first.CodexHome, "default adjacent index root");
            Equal(second.SettingsPath, Path.Combine(localTwo, "CodexTokenMonitor", "settings.json"), "settings belong to the current user");
            Check(!Directory.Exists(userOne) && !Directory.Exists(userTwo), "resolving paths created user data directories");

            foreach (var name in names) Environment.SetEnvironmentVariable(name, " \t ");
            var blank = new MonitorPaths(application, userOne, localOne);
            Equal(blank.CodexHome, first.CodexHome, "blank home ignored");
            Equal(blank.SessionRoot, first.SessionRoot, "blank sessions ignored");
            Equal(blank.SettingsPath, first.SettingsPath, "blank settings ignored");
            Environment.SetEnvironmentVariable(variable, Path.Combine(fixtureRoot, "expanded directory"));
            var literalUser = Path.Combine(fixtureRoot, "literal-$" + variable);
            Equal(new MonitorPaths(application, literalUser, localOne).CodexHome, Path.Combine(literalUser, ".codex"), "system supplied user paths are not expanded as configuration");
            foreach (var prefix in new[] { $"%{variable}%", "$" + variable, "${" + variable + "}" })
                Equal(MonitorPaths.ResolvePath(prefix + "/nested", application), Path.Combine(fixtureRoot, "expanded directory", "nested"), "environment variable expansion");
            Equal(MonitorPaths.ResolvePath("relative/../settings.json", application), Path.Combine(application, "settings.json"), "relative path ignores working directory");
            Equal(MonitorPaths.ResolvePath("settings.json"), Path.Combine(AppContext.BaseDirectory, "settings.json"), "default anchor is the executable directory");
            Equal(MonitorPaths.ResolvePath("~"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "public tilde home expansion");
            Environment.SetEnvironmentVariable("CODEX_HOME", "~/.codex-custom");
            Environment.SetEnvironmentVariable("CODEX_TOKEN_MONITOR_SETTINGS_PATH", "~/settings/preferences.json");
            var tilde = new MonitorPaths(application, userTwo, localTwo);
            Equal(tilde.CodexHome, Path.Combine(userTwo, ".codex-custom"), "tilde uses the selected user");
            Equal(tilde.SettingsPath, Path.Combine(userTwo, "settings", "preferences.json"), "tilde settings path");

            Environment.SetEnvironmentVariable("CODEX_HOME", "profiles/codex");
            Environment.SetEnvironmentVariable("CODEX_TOKEN_MONITOR_SESSION_ROOT", "independent/sessions");
            Environment.SetEnvironmentVariable("CODEX_TOKEN_MONITOR_SETTINGS_PATH", "preferences.json");
            var independent = new MonitorPaths(application, userOne, localOne);
            Equal(independent.CodexHome, Path.Combine(application, "profiles", "codex"), "relative home");
            Equal(independent.SessionRoot, Path.Combine(application, "independent", "sessions"), "independent sessions override");
            Equal(independent.WatchRoot, Path.Combine(application, "independent"), "override keeps its adjacent index and database");
            Equal(independent.SettingsPath, Path.Combine(application, "preferences.json"), "bare settings filename resolves absolutely");
            Environment.SetEnvironmentVariable("CODEX_TOKEN_MONITOR_SESSION_ROOT", "independent/sessions/");
            Equal(new MonitorPaths(application, userOne, localOne).WatchRoot, independent.WatchRoot, "trailing separator keeps the adjacent index root");
            Environment.SetEnvironmentVariable("CODEX_TOKEN_MONITOR_SESSION_ROOT", "independent/sessions");

            const string id = "77777777-7777-4777-8777-777777777777";
            const string model = "gpt-6-astra";
            var at = new DateTime(2026, 9, 5, 6, 0, 0, DateTimeKind.Utc);
            string Record(string type, object payload) => JsonSerializer.Serialize(new { type, timestamp = at.ToString("O"), payload }) + "\n";
            Directory.CreateDirectory(independent.SessionRoot);
            Directory.CreateDirectory(independent.CodexHome);
            var rollout = Path.Combine(independent.SessionRoot, "rollout-" + id + ".jsonl");
            var index = Path.Combine(independent.WatchRoot, "session_index.jsonl");
            File.WriteAllText(rollout, Record("session_meta", new { id, thread_source = "user" })
                + Record("event_msg", new { type = "task_started", turn_id = "turn-1" })
                + Record("turn_context", new { model, turn_id = "turn-1" }));
            File.WriteAllText(index, JsonSerializer.Serialize(new { id, thread_name = "Adjacent synthetic title" }) + "\n");
            File.WriteAllText(Path.Combine(independent.CodexHome, "session_index.jsonl"), JsonSerializer.Serialize(new { id, thread_name = "Wrong home title" }) + "\n");
            MarkReadOnly(rollout); MarkReadOnly(index);
            var parser = new SessionParser(independent);
            Equal(parser.WatchRoot, independent.WatchRoot, "parser uses the resolved watch root");
            Check(parser.GetRecentSessionChoices().Single().Label == "Adjacent synthetic title", "independent sessions read the wrong index");
            Check(parser.Parse(rollout).ModelEvidence is null, "missing database should leave model unverified");

            var database = Path.Combine(independent.WatchRoot, "logs_2.sqlite");
            if (OperatingSystem.IsWindows())
            {
                var stamp = new DateTimeOffset(at).ToUnixTimeSeconds();
                NativeModelFixture.Create(database, "CREATE TABLE logs(id INTEGER PRIMARY KEY,ts INTEGER,ts_nanos INTEGER,target TEXT,thread_id TEXT,feedback_log_body TEXT);"
                    + $"INSERT INTO logs VALUES(1,{stamp},0,'codex_core::session','{id}','turn{{turn.id=turn-1 model={model}}}: server reported model {model} (matches requested model)');");
                MarkReadOnly(database);
                Check(parser.Parse(rollout).ModelEvidence?.ReturnedModel == model, "blank model log override should use adjacent database");
                Environment.SetEnvironmentVariable("CODEX_TOKEN_MONITOR_MODEL_LOG_PATH", Path.GetRelativePath(AppContext.BaseDirectory, database));
                Check(ModelResponseLogReader.ReadLatest(independent.CodexHome, id, "turn-1", model, at)?.ReturnedModel == model, "relative model log override used the working directory");
                Environment.SetEnvironmentVariable(variable, independent.WatchRoot);
                Environment.SetEnvironmentVariable("CODEX_TOKEN_MONITOR_MODEL_LOG_PATH", $"%{variable}%/logs_2.sqlite");
                Check(ModelResponseLogReader.ReadLatest(independent.CodexHome, id, "turn-1", model, at)?.ReturnedModel == model, "model log override did not expand variables");
            }
            Environment.SetEnvironmentVariable("CODEX_TOKEN_MONITOR_MODEL_LOG_PATH", Path.Combine(fixtureRoot, "missing.sqlite"));
            Check(ModelResponseLogReader.ReadLatest(independent.WatchRoot, id, "turn-1", model, at) is null, "missing overridden database should leave model unverified");
            Environment.SetEnvironmentVariable(variable, " \t ");
            Environment.SetEnvironmentVariable("CODEX_TOKEN_MONITOR_MODEL_LOG_PATH", "$" + variable);
            Check(ModelResponseLogReader.ReadLatest(independent.WatchRoot, id, "turn-1", model, at) is null, "invalid expanded model log path should leave model unverified");
            var before = Directory.GetFiles(independent.WatchRoot, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            parser.Parse(rollout); parser.GetRecentSessionChoices(); parser.GetLatestAccountUsage();
            Check(before.Keys.Order().SequenceEqual(Directory.GetFiles(independent.WatchRoot, "*", SearchOption.AllDirectories).Order()), "reading Codex data created or removed files");
            foreach (var (path, bytes) in before) Check(bytes.SequenceEqual(File.ReadAllBytes(path)), "reading modified Codex data: " + Path.GetFileName(path));

            Environment.SetEnvironmentVariable("CODEX_TOKEN_MONITOR_SETTINGS_PATH", Path.GetFileName(bareSettings));
            new MonitorSettings { Topmost = true, ShowRemainingQuota = true, Left = 321, Top = 123 }.Save();
            Check(File.Exists(bareSettings) && !File.Exists(Path.Combine(otherDirectory, Path.GetFileName(bareSettings))), "bare filename settings did not save next to the executable");
            var loaded = MonitorSettings.Load();
            Check(loaded.Topmost && loaded.ShowRemainingQuota && loaded.Left == 321 && loaded.Top == 123, "bare filename settings roundtrip");
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
            foreach (var path in readOnlyFiles) if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.Delete(bareSettings); File.Delete(bareSettings + ".tmp");
        }

        void MarkReadOnly(string path)
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            readOnlyFiles.Add(path);
        }
    }

    public static void RunStartupOptions()
    {
        var names = new[] { "CODEX_HOME", "CODEX_TOKEN_MONITOR_SESSION_ROOT", "CODEX_TOKEN_MONITOR_SETTINGS_PATH", "CODEX_TOKEN_MONITOR_MODEL_LOG_PATH" };
        var saved = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var persistent = names.SelectMany(name => new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }
            .Select(target => (Name: name, Target: target, Value: Environment.GetEnvironmentVariable(name, target)))).ToList();
        var fixtureRoot = Path.Combine(Path.GetDirectoryName(saved["CODEX_TOKEN_MONITOR_SESSION_ROOT"]!)!, "startup-fixtures");
        try
        {
            var inheritedHome = Path.Combine(fixtureRoot, "inherited-home");
            var inheritedSessions = Path.Combine(fixtureRoot, "inherited-sessions");
            var inheritedSettings = Path.Combine(fixtureRoot, "inherited-settings.json");
            Environment.SetEnvironmentVariable("CODEX_HOME", inheritedHome);
            Environment.SetEnvironmentVariable("CODEX_TOKEN_MONITOR_SESSION_ROOT", inheritedSessions);
            Environment.SetEnvironmentVariable("CODEX_TOKEN_MONITOR_SETTINGS_PATH", inheritedSettings);
            Environment.SetEnvironmentVariable("CODEX_TOKEN_MONITOR_MODEL_LOG_PATH", null);
            var homeOverride = Path.Combine(fixtureRoot, "new-home");
            var sessionsOverride = Path.Combine(fixtureRoot, "new-sessions");
            var settingsOverride = Path.Combine(fixtureRoot, "new-settings.json");
            var logOverride = Path.Combine(fixtureRoot, "missing-log.sqlite");
            var relativeHome = Path.GetRelativePath(AppContext.BaseDirectory, homeOverride);
            var onlyHome = StartupOptions.Apply(["--codex-home=ignored-home", "--CODEX-HOME=" + relativeHome]);
            Equal(onlyHome.CodexHome, homeOverride, "last home argument wins, case insensitively");
            Equal(onlyHome.SessionRoot, Path.Combine(homeOverride, "sessions"), "explicit home replaces inherited sessions override");
            Equal(onlyHome.SettingsPath, inheritedSettings, "unmentioned settings remain inherited");
            Check(Environment.GetEnvironmentVariable("CODEX_TOKEN_MONITOR_SESSION_ROOT") is null, "obsolete inherited sessions override was retained");
            var combined = StartupOptions.Apply(["--codex-home=" + homeOverride, "--sessions=" + sessionsOverride,
                "--portable", "--settings=" + settingsOverride, "--model-log=" + Path.GetRelativePath(AppContext.BaseDirectory, logOverride)]);
            Equal(combined.SessionRoot, sessionsOverride, "explicit sessions override wins with explicit home");
            Equal(combined.SettingsPath, settingsOverride, "explicit settings wins over portable mode");
            Equal(Environment.GetEnvironmentVariable("CODEX_TOKEN_MONITOR_MODEL_LOG_PATH")!, logOverride, "model log override is normalized for this process");
            var portable = StartupOptions.Apply(["--portable"]);
            Equal(portable.SettingsPath, Path.Combine(AppContext.BaseDirectory, "data", "settings.json"), "portable settings replace inherited settings");
            var settingsLast = StartupOptions.Apply(["--settings=ignored.json", "--settings=" + settingsOverride, "--PORTABLE"]);
            Equal(settingsLast.SettingsPath, settingsOverride, "explicit settings also wins when portable appears last");
            Check(StartupOptions.Value(["--settings= ", "--settings=final.json"], "--settings") == "final.json", "discarded earlier value should not affect the final override");
            foreach (var option in new[] { "--codex-home", "--sessions", "--settings", "--model-log" })
            {
                var before = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
                var rejected = false;
                try { StartupOptions.Apply([option + "= \t \""]); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "blank override was not rejected: " + option);
                foreach (var name in names) Check(Environment.GetEnvironmentVariable(name) == before[name], "invalid options partially changed process environment");
            }
            Check(!Directory.Exists(fixtureRoot), "applying startup options created Codex data directories");
            foreach (var item in persistent)
                Check(Environment.GetEnvironmentVariable(item.Name, item.Target) == item.Value, "startup options changed persistent environment variables");
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static void Equal(string actual, string expected, string message)
        => Check(string.Equals(actual, Path.GetFullPath(expected), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), message + ": " + actual);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}

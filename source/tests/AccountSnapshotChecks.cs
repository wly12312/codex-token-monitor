using System.Text;
using System.Text.Json;
using CodexTokenMonitor;

internal static class AccountSnapshotChecks
{
    public static void Run()
    {
        var root = Environment.GetEnvironmentVariable("CODEX_TOKEN_MONITOR_SESSION_ROOT")
            ?? throw new InvalidOperationException("A synthetic session root is required.");
        var path = Path.Combine(root, "rollout-account-snapshots.jsonl");
        var at = new DateTime(2026, 9, 22, 6, 0, 0, DateTimeKind.Utc);
        var writeStamp = at;
        var parser = new SessionParser();
        string Limits(double used, DateTime observed) => JsonSerializer.Serialize(new
        {
            timestamp = observed.ToString("O"), type = "event_msg",
            payload = new
            {
                type = "token_count", info = (object?)null,
                rate_limits = new
                {
                    limit_id = "codex",
                    primary = new { used_percent = used, window_minutes = 300, resets_at = 1893456000 }
                }
            }
        }) + "\n";
        string ToolOutput(int size) => JsonSerializer.Serialize(new
        {
            timestamp = at.ToString("O"), type = "response_item",
            payload = new { type = "function_call_output", output = new string('x', size) }
        }) + "\n";
        void Write(string text)
        {
            File.WriteAllText(path, text, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, writeStamp = writeStamp.AddSeconds(1));
        }
        void Append(string text)
        {
            File.AppendAllText(path, text, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, writeStamp = writeStamp.AddSeconds(1));
        }
        void Expect(double? used, string scenario)
        {
            var actual = parser.GetLatestAccountUsage()?.FiveHour;
            if (actual != used) throw new Exception($"{scenario}: expected {used?.ToString() ?? "no snapshot"}, got {actual?.ToString() ?? "no snapshot"}.");
        }

        Write(Limits(27, at));
        Expect(27, "initial snapshot");
        Append(ToolOutput(300_000));
        Expect(27, "large tool output must not erase an observed snapshot");
        Expect(27, "unchanged large file must keep its cached snapshot");
        Append(Limits(35, at.AddMinutes(1)));
        Expect(35, "a newer snapshot must replace the retained value");
        Append(Limits(10, at.AddMinutes(-1)));
        Expect(35, "an appended older event must not move account usage backwards");
        Append(ToolOutput(300_000));
        Expect(35, "the newest observation must survive another large append");

        var sameLength = checked((int)new FileInfo(path).Length);
        Write(new string('z', sameLength - 1) + "\n");
        Expect(null, "same-length replacement must invalidate the old snapshot");
        Write(Limits(41, at.AddMinutes(2)));
        Expect(41, "snapshot after replacement");
        Write("{}\n");
        Expect(null, "truncation must invalidate the old snapshot");

        Write(Limits(43, at.AddMinutes(3)));
        Expect(43, "snapshot before a longer replacement");
        Write(ToolOutput(300_000));
        Expect(null, "longer replacement must not be mistaken for an append");
        Write(Limits(47, at.AddMinutes(4)));
        Expect(47, "snapshot before replacement with an earlier observation");
        Write(Limits(11, at.AddMinutes(-4)) + ToolOutput(1000));
        Expect(11, "a replacement file must use its own observation even when older");
    }
}

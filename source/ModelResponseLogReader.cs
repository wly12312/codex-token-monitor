using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexTokenMonitor;

// Read-only access through the SQLite runtime shipped with Windows. No extra package,
// network requests, log configuration changes, or access to authentication files.
public static class ModelResponseLogReader
{
    private const string Sql = "SELECT ts, ts_nanos, target, feedback_log_body FROM logs WHERE thread_id=?1 "
        + "AND target IN ('codex_core::session','codex_core::codex','codex_core::session::session','codex_core::stream_events_utils') "
        + "AND feedback_log_body LIKE '%server reported model %' AND ts>=?2 ORDER BY ts DESC,ts_nanos DESC,id DESC LIMIT 50";
    private static readonly Regex Report = new(@"(?:^|:\s+)server reported model (?<returned>[a-zA-Z0-9_.-]+) (?:\(matches requested model\)|while requested model was (?<requested>[a-zA-Z0-9_.-]+))(?:\s+thread_id=[a-zA-Z0-9-]+)?\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex Turn = new("(?:turn\\.id|turn_id)=\"?(?<id>[a-zA-Z0-9-]+)", RegexOptions.CultureInvariant);

    public static ModelResponseEvidence? ParseReport(string target, string body, string turnId, string requested, DateTime at)
    {
        if (target is not ("codex_core::session" or "codex_core::codex" or "codex_core::session::session" or "codex_core::stream_events_utils")) return null;
        if (string.IsNullOrEmpty(turnId) || string.IsNullOrEmpty(requested) || body.Contains("ToolCall:", StringComparison.Ordinal)) return null;
        var match = Report.Match(body);
        if (!match.Success || !Turn.Matches(body[..match.Index]).Any(m => m.Groups["id"].Value == turnId)) return null;
        var returned = match.Groups["returned"].Value;
        var from = match.Groups["requested"].Success ? match.Groups["requested"].Value : returned;
        if (!string.Equals(from, requested, StringComparison.OrdinalIgnoreCase)) return null;
        return new(requested, returned, turnId, at, "运行日志 · server reported model");
    }

    public static ModelResponseEvidence? ReadLatest(string root, string sessionId, string turnId, string requested, DateTime notBefore)
    {
        if (turnId.Length == 0 || requested.Length == 0 || sessionId.Length == 0) return null;
        ModelResponseEvidence? latest = null;
        var overridePath = MonitorPaths.EnvironmentPath("CODEX_TOKEN_MONITOR_MODEL_LOG_PATH");
        string[] paths;
        try { paths = overridePath is not null ? new[] { MonitorPaths.ResolvePath(overridePath) } : new[] { Path.Combine(root, "logs_2.sqlite"), Path.Combine(root, "sqlite", "logs_2.sqlite") }; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            IntPtr db = IntPtr.Zero, statement = IntPtr.Zero;
            try
            {
                if (sqlite3_open_v2(Utf8(path), out db, 1 /* SQLITE_OPEN_READONLY */, IntPtr.Zero) != 0) continue;
                sqlite3_busy_timeout(db, 100);
                if (sqlite3_prepare_v2(db, Utf8(Sql), -1, out statement, IntPtr.Zero) != 0) continue;
                var id = Utf8(sessionId);
                if (sqlite3_bind_text(statement, 1, id, id.Length - 1, new IntPtr(-1)) != 0) continue;
                if (sqlite3_bind_int64(statement, 2, new DateTimeOffset(DateTime.SpecifyKind(notBefore, DateTimeKind.Utc)).ToUnixTimeSeconds()) != 0) continue;
                while (sqlite3_step(statement) == 100 /* SQLITE_ROW */)
                {
                    var at = DateTimeOffset.FromUnixTimeSeconds(sqlite3_column_int64(statement, 0)).UtcDateTime.AddTicks(sqlite3_column_int64(statement, 1) / 100);
                    if (at < notBefore) continue;
                    var target = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 2)) ?? "";
                    var body = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 3)) ?? "";
                    var result = ParseReport(target, body, turnId, requested, at);
                    if (result is null) continue;
                    if (latest is null || result.ObservedAt > latest.ObservedAt) latest = result;
                    break;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or ArgumentOutOfRangeException or IOException or UnauthorizedAccessException) { }
            finally
            {
                if (statement != IntPtr.Zero) sqlite3_finalize(statement);
                if (db != IntPtr.Zero) sqlite3_close(db);
            }
        }
        return latest;
    }
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + '\0');
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] value, int length, IntPtr destructor);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern long sqlite3_column_int64(IntPtr statement, int index);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int index);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
}

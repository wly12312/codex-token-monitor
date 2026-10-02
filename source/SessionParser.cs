using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexTokenMonitor;

// Used serially on a worker thread. All returned rows are independent snapshots.
public sealed class SessionParser
{
    private readonly string _sessionRoot, _indexPath;
    private readonly Dictionary<string, ParseState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long, DateTime, SessionMeta?)> _meta = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LimitFileState> _limits = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _titles = new(StringComparer.OrdinalIgnoreCase);
    private (long, DateTime) _indexStamp;
    public long BytesRead { get; private set; }
    public string WatchRoot { get; }
    public SessionParser(MonitorPaths? paths = null)
    {
        paths ??= new MonitorPaths();
        _sessionRoot = paths.SessionRoot;
        WatchRoot = paths.WatchRoot;
        _indexPath = Path.Combine(WatchRoot, "session_index.jsonl");
    }
    private List<FileInfo> Files()
    {
        if (!Directory.Exists(_sessionRoot)) return [];
        try { return new DirectoryInfo(_sessionRoot).EnumerateFiles("rollout-*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList(); }
        catch (IOException) { return []; }
    }
    public IReadOnlyList<FileInfo> GetRecentUserSessions(int limit = 20) => Files().OrderByDescending(f => f.LastWriteTimeUtc).Where(f => Meta(f)?.Source == "user").Take(Math.Max(0, limit)).ToList();
    public string? GetLatestUserSession() => GetRecentUserSessions(1).FirstOrDefault()?.FullName;
    public IReadOnlyList<SessionChoice> GetRecentSessionChoices(int limit = 20)
    {
        LoadTitles();
        return GetRecentUserSessions(limit).Select(file => new SessionChoice
        {
            Path = file.FullName, SessionId = Meta(file)!.Id, Updated = file.LastWriteTime,
            Label = _titles.GetValueOrDefault(Meta(file)!.Id) ?? $"Untitled task · {file.LastWriteTime:MM-dd HH:mm}"
        }).ToList();
    }
    private SessionMeta? Meta(FileInfo file)
    {
        try
        {
            if (_meta.TryGetValue(file.FullName, out var saved) && saved.Item1 == file.Length && saved.Item2 == file.LastWriteTimeUtc) return saved.Item3;
            using var stream = Open(file.FullName);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var doc = JsonDocument.Parse(reader.ReadLine() ?? "{}");
            var r = doc.RootElement;
            if (Text(r, "type") != "session_meta") return null;
            var id = Text(r, "payload", "id") ?? Text(r, "payload", "session_id") ?? "";
            // Forks may retain ancestor metadata; the rollout filename identifies this task.
            var stem = Path.GetFileNameWithoutExtension(file.Name);
            if (stem.Length >= 36 && Guid.TryParse(stem[^36..], out _)) id = stem[^36..];
            var source = Text(r, "payload", "thread_source") ?? "";
            var parent = Text(r, "payload", "parent_thread_id") ?? Text(r, "payload", "source", "subagent", "thread_spawn", "parent_thread_id") ?? "";
            if (source.Length == 0)
            {
                if (Get(r, out _, "payload", "source", "subagent")) source = "subagent";
                else if (Text(r, "payload", "source") is "cli" or "vscode" or "exec") source = "user";
            }
            if (Get(r, out var sub, "payload", "source", "subagent") && sub.ToString().Contains("guardian", StringComparison.OrdinalIgnoreCase)) source = "guardian_review";
            var result = new SessionMeta(file.FullName, id, parent, source);
            _meta[file.FullName] = (file.Length, file.LastWriteTimeUtc, result);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public SessionSummary Parse(string path)
    {
        LoadTitles();
        var summary = ParseSingle(path);
        if (summary.SessionId.Length == 0) return summary;
        var children = Files().Select(Meta).Where(m => m is not null && m.Source == "subagent" && m.ParentId.Length > 0).Cast<SessionMeta>().ToLookup(m => m.ParentId, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(); queue.Enqueue(summary.SessionId);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { summary.SessionId };
        var counted = new HashSet<string>();
        while (queue.Count > 0)
        {
            foreach (var child in children[queue.Dequeue()])
            {
                if (!visited.Add(child.Id)) continue;
                queue.Enqueue(child.Id); summary.AgentPaths.Add(child.Path);
                try
                {
                    var data = ParseSingle(child.Path);
                    foreach (var row in data.Rows.Where(r => !InternalModel(r.ModelId)))
                    {
                        row.IsSubagent = true; row.SourceSessionId = child.Id; row.ParentSessionId = child.ParentId;
                        summary.AgentRows.Add(row); counted.Add(child.Id);
                    }
                    summary.MalformedLines += data.MalformedLines;
                    if (data.LastWriteTime > summary.LastWriteTime) summary.LastWriteTime = data.LastWriteTime;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        summary.AgentRows = summary.AgentRows.OrderBy(r => r.Started).ToList();
        for (var i = 0; i < summary.AgentRows.Count; i++) summary.AgentRows[i].Number = i + 1;
        summary.AgentSessionCount = counted.Count;
        var keep = new HashSet<string>(summary.AgentPaths, StringComparer.OrdinalIgnoreCase) { path };
        foreach (var key in _states.Keys.Where(k => !keep.Contains(k)).ToList()) _states.Remove(key);
        var reported = ModelResponseLogReader.ReadLatest(WatchRoot, summary.SessionId, summary.LatestTurnId, summary.RequestedModel, summary.ModelRequestAt);
        if (reported is not null && (summary.ModelEvidence is null || reported.ObservedAt > summary.ModelEvidence.ObservedAt)) summary.ModelEvidence = reported;
        return summary;
    }
    public AccountUsageSnapshot? GetLatestAccountUsage()
    {
        AccountUsageSnapshot? best = null;
        foreach (var file in Files().OrderByDescending(f => f.LastWriteTimeUtc).Take(30))
        {
            try
            {
                AccountUsageSnapshot? snapshot = null;
                if (_limits.TryGetValue(file.FullName, out var saved) && saved.Length == file.Length && saved.WriteUtc == file.LastWriteTimeUtc && saved.CreatedUtc == file.CreationTimeUtc) snapshot = saved.Snapshot;
                else
                {
                    using var stream = Open(file.FullName);
                    var length = stream.Length;
                    AccountUsageSnapshot? previous = null;
                    // A large tool result can push a still-valid snapshot outside the tail.
                    // Retain it only when the old end of the file survived an append.
                    if (saved is not null && length > saved.Length && saved.CreatedUtc == file.CreationTimeUtc
                        && ReadTailAnchor(stream, saved.Length).AsSpan().SequenceEqual(saved.TailAnchor)) previous = saved.Snapshot;
                    var tailAnchor = ReadTailAnchor(stream, length);
                    stream.Seek(Math.Max(0, length - 262144), SeekOrigin.Begin);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    foreach (var line in reader.ReadToEnd().Split('\n').Reverse())
                    {
                        if (!line.Contains("rate_limits", StringComparison.Ordinal)) continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            if (Get(doc.RootElement, out var limits, "payload", "rate_limits"))
                            {
                                var candidate = ParseLimits(limits, Date(Text(doc.RootElement, "timestamp")));
                                if (candidate is not null && (snapshot is null || candidate.ObservedAt > snapshot.ObservedAt)) snapshot = candidate;
                            }
                        }
                        catch (JsonException) { }
                    }
                    if (previous is not null && (snapshot is null || previous.ObservedAt > snapshot.ObservedAt)) snapshot = previous;
                    _limits[file.FullName] = new(length, file.LastWriteTimeUtc, file.CreationTimeUtc, tailAnchor, snapshot);
                }
                if (snapshot is not null && (best is null || snapshot.ObservedAt > best.ObservedAt)) best = snapshot;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (_limits.Count > 100) _limits.Clear();
        if (_meta.Count > 10000) _meta.Clear();
        return best;
    }
    private static byte[] ReadTailAnchor(FileStream stream, long end)
    {
        var anchor = new byte[(int)Math.Min(64, end)];
        stream.Position = end - anchor.Length;
        stream.ReadExactly(anchor);
        return anchor;
    }
    private SessionSummary ParseSingle(string path)
    {
        var file = new FileInfo(path);
        if (!_states.TryGetValue(path, out var s) || file.Length < s.Length || (file.Length == s.Length && file.LastWriteTimeUtc != s.WriteUtc) || file.CreationTimeUtc != s.CreatedUtc)
        {
            s = new ParseState { CreatedUtc = file.CreationTimeUtc }; _states[path] = s;
        }
        using (var stream = Open(path))
        {
            stream.Position = s.Offset;
            var buffer = new byte[65536]; using var pending = new MemoryStream(); int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                BytesRead += read; var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    pending.Write(buffer, start, i - start); ProcessLine(pending.ToArray(), s); pending.SetLength(0);
                    s.Offset = stream.Position - read + i + 1; start = i + 1;
                }
                pending.Write(buffer, start, read - start);
            }
            // Retry a partially written record from its beginning next time.
            if (pending.Length > 0 && ProcessLine(pending.ToArray(), s, true)) s.Offset = stream.Position;
            s.Length = stream.Length;
        }
        s.WriteUtc = file.LastWriteTimeUtc;
        var rows = s.Rows.Select(r => r.Clone()).ToList();
        if (s.Active is { HasUsage: true }) rows.Add(Snapshot(s.Active, true));
        for (var i = 0; i < rows.Count; i++) rows[i].Number = i + 1;
        var id = Meta(file)?.Id ?? s.Id;
        return new SessionSummary
        {
            Path = path, SessionId = id, Title = _titles.GetValueOrDefault(id) ?? (s.TitlePreview.Length > 0 ? s.TitlePreview : "Untitled task"),
            CurrentModel = s.Model, Rows = rows, Totals = s.Totals.Clone(), TotalCredits = rows.Sum(r => r.Credits), TotalUsd = rows.Sum(r => r.Usd),
            RequestedModel = s.RequestedModel, LatestTurnId = s.LatestTurnId, ModelRequestAt = s.ModelRequestAt, ModelEvidence = s.ModelEvidence,
            FiveHour = s.Limits?.FiveHour, Weekly = s.Limits?.Weekly, AccountUsage = s.Limits,
            ContextWindow = s.Window, LastRequestInput = s.LastInput, ContextObservedAt = s.ContextAt, CompactionCount = s.Compactions, MalformedLines = s.Malformed, LastWriteTime = file.LastWriteTime
        };
    }
    private static bool ProcessLine(byte[] bytes, ParseState s, bool partial = false)
    {
        var line = Encoding.UTF8.GetString(bytes).Trim('\uFEFF', '\r', ' ', '\t');
        if (line.Length == 0) return true;
        try
        {
            using var doc = JsonDocument.Parse(line); var r = doc.RootElement;
            var type = Text(r, "type"); var at = Date(Text(r, "timestamp"));
            if (type == "session_meta")
            {
                s.Id = Text(r, "payload", "id") ?? Text(r, "payload", "session_id") ?? "";
                s.Model = Text(r, "payload", "base_instructions", "provenance", "model") ?? s.Model;
            }
            else if (type == "turn_context" || (type == "event_msg" && Text(r, "payload", "type") == "thread_settings_applied"))
            {
                var requested = Text(r, "payload", "model") ?? Text(r, "payload", "thread_settings", "model");
                var model = requested ?? s.Model; var tier = Text(r, "payload", "service_tier") ?? Text(r, "payload", "thread_settings", "service_tier");
                var turn = Text(r, "payload", "turn_id");
                if (requested is not null && (!string.Equals(requested, s.RequestedModel, StringComparison.OrdinalIgnoreCase) || (turn is not null && turn != s.LatestTurnId)))
                { s.ModelEvidence = null; s.ModelRequestAt = at; }
                if (requested is not null) s.RequestedModel = requested;
                if (turn is not null) s.LatestTurnId = turn;
                if (s.Active is { HasUsage: true } active && !SameModel(active.Model, model))
                {
                    Finish(s, false); s.Active = new TurnState { Id = active.Id, Started = at, Model = model, Tier = tier };
                }
                s.Model = model; s.Tier = tier;
                if (s.Active is not null) { s.Active.Model = model; s.Active.Tier = tier; }
            }
            else if (type == "response_item" && s.TitlePreview.Length == 0 && Text(r, "payload", "role") == "user")
            {
                if (Get(r, out var content, "payload", "content") && content.ValueKind == JsonValueKind.Array) SetPrompt(s, string.Join(" ", content.EnumerateArray().Select(p => Text(p, "text") ?? Text(p, "input_text") ?? "")));
            }
            else if (type == "compacted") s.Compactions++;
            else if (type == "event_msg")
            {
                switch (Text(r, "payload", "type"))
                {
                    case "task_started":
                        Finish(s, true); s.LatestTurnId = Text(r, "payload", "turn_id") ?? ""; s.ModelEvidence = null; s.ModelRequestAt = at;
                        s.Active = new TurnState { Id = s.LatestTurnId, Started = at, Model = s.Model, Tier = s.Tier }; break;
                    case "model_reroute":
                        var from = Text(r, "payload", "from_model"); var to = Text(r, "payload", "to_model");
                        var rerouteTurn = Text(r, "payload", "turn_id") ?? s.LatestTurnId;
                        if (from == s.RequestedModel && !string.IsNullOrWhiteSpace(to) && rerouteTurn.Length > 0 && rerouteTurn == s.LatestTurnId && at >= s.ModelRequestAt)
                            s.ModelEvidence = new(from, to, rerouteTurn, at, "session model_reroute");
                        break;
                    case "user_message": SetPrompt(s, Text(r, "payload", "message") ?? ""); break;
                    case "token_count": TokenEvent(r, s, at); break;
                    case "task_complete": Finish(s, false); break;
                    case "turn_aborted": Finish(s, true); break;
                }
            }
            return true;
        }
        catch (JsonException) { if (!partial) s.Malformed++; return !partial; }
    }
    private static void TokenEvent(JsonElement r, ParseState s, DateTime at)
    {
        if (Get(r, out var limits, "payload", "rate_limits")) s.Limits = ParseLimits(limits, at) ?? s.Limits;
        if (!Get(r, out var info, "payload", "info") || info.ValueKind != JsonValueKind.Object) return;
        s.Window = Number(info, "model_context_window") ?? s.Window;
        TokenUsage? last = Get(info, out var rawLast, "last_token_usage") && rawLast.ValueKind == JsonValueKind.Object ? Usage(rawLast) : null;
        if (last is not null) { s.LastInput = last.Input; s.ContextAt = at; }
        if (!Get(info, out var rawTotal, "total_token_usage") || rawTotal.ValueKind != JsonValueKind.Object) return;
        var total = Usage(rawTotal);
        if (s.Previous is not null && total.SameCounters(s.Previous)) return;
        var reset = s.Previous is not null && (total.Input < s.Previous.Input || total.Output < s.Previous.Output || total.Total < s.Previous.Total);
        // Prefer the billed request: cumulative counters can rebase after compaction.
        var usage = last ?? (s.Previous is null || reset ? total : TokenUsage.Delta(total, s.Previous));
        s.Previous = total; s.Totals.Add(usage);
        if (usage.Total == 0 && usage.Input == 0 && usage.Output == 0) return;
        s.Active ??= new TurnState { Id = "resumed-" + at.Ticks, Started = at, Model = s.Model, Tier = s.Tier };
        var a = s.Active; a.Usage.Add(usage); a.HasUsage = true;
        var price = PricingCatalog.Calculate(a.Model, usage, a.Tier, last?.Input);
        a.Credits += price.Credits; a.Usd += price.Usd; a.Known &= price.Known; a.Notes.Add(price.Note); a.Limits = s.Limits;
    }
    private static void SetPrompt(ParseState s, string text)
    {
        text = text.Trim();
        if (s.Active is null || s.TitlePreview.Length > 0 || text.Length == 0 || text.StartsWith('<')) return;
        // Only one title fallback per session, never a prompt copy for each turn.
        s.TitlePreview = text.Length > 100 ? text[..100] + "…" : text;
    }
    private static void Finish(ParseState s, bool aborted)
    {
        if (s.Active is { HasUsage: true }) { var row = Snapshot(s.Active, false); row.IsAborted = aborted; s.Rows.Add(row); }
        s.Active = null;
    }
    private static TurnRow Snapshot(TurnState a, bool live) => new()
    {
        TurnId = a.Id, Started = a.Started, IsLive = live, ModelId = a.Model, Model = PricingCatalog.DisplayName(a.Model),
        Input = a.Usage.Input, Cached = a.Usage.Cached, CacheWrite = a.Usage.CacheWrite, Uncached = Math.Max(0, a.Usage.Input - a.Usage.Cached), Output = a.Usage.Output,
        Reasoning = a.Usage.Reasoning, Total = a.Usage.Total, Credits = a.Credits, Usd = a.Usd, PricingKnown = a.Known, PricingNote = string.Join("\n", a.Notes), FiveHour = a.Limits?.FiveHour, Weekly = a.Limits?.Weekly
    };
    private void LoadTitles()
    {
        try
        {
            var file = new FileInfo(_indexPath); if (!file.Exists) { _titles.Clear(); return; }
            var stamp = (file.Length, file.LastWriteTimeUtc); if (stamp == _indexStamp) return;
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var stream = Open(_indexPath); using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line); var id = Text(doc.RootElement, "id"); var title = Text(doc.RootElement, "thread_name") ?? Text(doc.RootElement, "title");
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(title)) titles[id] = title.Trim().Replace('\r', ' ').Replace('\n', ' ');
                }
                catch (JsonException) { }
            }
            _titles = titles; _indexStamp = stamp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static AccountUsageSnapshot? ParseLimits(JsonElement r, DateTime at)
    {
        var id = Text(r, "limit_id"); if (id is not null && id != "codex") return null;
        var result = new AccountUsageSnapshot { ObservedAt = at, Plan = Text(r, "plan_type") };
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!Get(r, out var b, name)) continue;
            var minutes = Number(b, "window_minutes"); var used = Decimal(b, "used_percent"); if (used is null) continue;
            var reset = Number(b, "resets_at"); DateTimeOffset? resets = reset is >= 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(reset.Value) : null;
            if (minutes is >= 250 and <= 360) { result.FiveHour = Math.Clamp(used.Value, 0, 100); result.FiveHourResetsAt = resets; }
            else if (minutes is >= 9000 and <= 11000) { result.Weekly = Math.Clamp(used.Value, 0, 100); result.WeeklyResetsAt = resets; }
        }
        return result.FiveHour.HasValue || result.Weekly.HasValue ? result : null;
    }
    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    private static bool InternalModel(string m) => m.Contains("guardian", StringComparison.OrdinalIgnoreCase) || m.Contains("auto-review", StringComparison.OrdinalIgnoreCase);
    public static bool SameModel(string a, string b) => PricingCatalog.CanonicalModel(a) == PricingCatalog.CanonicalModel(b);
    private static TokenUsage Usage(JsonElement r) => new()
    {
        Input = Math.Max(0, Number(r, "input_tokens") ?? 0), Cached = Math.Max(0, Number(r, "cached_input_tokens") ?? 0), CacheWrite = Math.Max(0, Number(r, "cache_write_input_tokens") ?? 0),
        Output = Math.Max(0, Number(r, "output_tokens") ?? 0), Reasoning = Math.Max(0, Number(r, "reasoning_output_tokens") ?? 0), Total = Math.Max(0, Number(r, "total_tokens") ?? ((Number(r, "input_tokens") ?? 0) + (Number(r, "output_tokens") ?? 0)))
    };
    private static DateTime Date(string? s) => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : DateTime.MinValue;
    private static string? Text(JsonElement r, params string[] p) => Get(r, out var v, p) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Number(JsonElement r, params string[] p) => Get(r, out var v, p) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    private static double? Decimal(JsonElement r, params string[] p) => Get(r, out var v, p) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    private static bool Get(JsonElement r, out JsonElement v, params string[] p)
    {
        v = r; foreach (var key in p) { if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty(key, out var next)) { v = default; return false; } v = next; } return true;
    }
    private sealed record SessionMeta(string Path, string Id, string ParentId, string Source);
    private sealed record LimitFileState(long Length, DateTime WriteUtc, DateTime CreatedUtc, byte[] TailAnchor, AccountUsageSnapshot? Snapshot);
    private sealed class ParseState
    {
        public long Offset, Length; public DateTime WriteUtc, CreatedUtc; public string Id = "", Model = "unknown", TitlePreview = ""; public string? Tier;
        public string RequestedModel = "", LatestTurnId = ""; public DateTime ModelRequestAt; public ModelResponseEvidence? ModelEvidence;
        public TokenUsage? Previous; public TokenUsage Totals = new(); public List<TurnRow> Rows = []; public TurnState? Active;
        public AccountUsageSnapshot? Limits; public long? Window, LastInput; public DateTime? ContextAt; public int Compactions, Malformed;
    }
    private sealed class TurnState
    {
        public string Id = "", Model = "unknown"; public string? Tier; public DateTime Started; public TokenUsage Usage = new();
        public bool HasUsage, Known = true; public double Credits, Usd; public HashSet<string> Notes = []; public AccountUsageSnapshot? Limits;
    }
}

using System.Text;
using System.Text.Json;
using CodexTokenMonitor;

var root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "codex-monitor-tests-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var results = new List<object>();
var failures = 0;
var mainId = "11111111-1111-4111-8111-111111111111";
var agentId = "22222222-2222-4222-8222-222222222222";
var nestedId = "33333333-3333-4333-8333-333333333333";
var at = DateTime.Parse("2026-09-05T06:00:00Z").ToUniversalTime();
string Record(string type, object payload) => JsonSerializer.Serialize(new { timestamp = at.ToString("O"), type, payload }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
string Event(string type) => Record("event_msg", new { type, turn_id = "turn-1" });
object Usage(long input, long cached = 0, long output = 100, long reasoning = 40, long writes = 0) => new { input_tokens = input, cached_input_tokens = cached, cache_write_input_tokens = writes, output_tokens = output, reasoning_output_tokens = reasoning, total_tokens = input + output };
string Tokens(object total, object? last, object? limits = null) => Record("event_msg", new { type = "token_count", info = new { total_token_usage = total, last_token_usage = last, model_context_window = 258400 }, rate_limits = limits });
string Context(string model, string? tier = null) => Record("turn_context", new { model, service_tier = tier });
string Meta(string id, string source = "user", string? parent = null) => Record("session_meta", new { id, thread_source = source, parent_thread_id = parent });
string FileFor(string id) => Path.Combine(Environment.GetEnvironmentVariable("CODEX_TOKEN_MONITOR_SESSION_ROOT")!, "rollout-2026-09-05T14-00-00-" + id + ".jsonl");
void Setup(string name)
{
    var folder = Path.Combine(root, name, "sessions"); Directory.CreateDirectory(folder);
    Environment.SetEnvironmentVariable("CODEX_TOKEN_MONITOR_SESSION_ROOT", folder);
}
void Write(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));
void Append(string path, string text) => File.AppendAllText(path, text, new UTF8Encoding(false));
void Check(bool value, string message) { if (!value) throw new Exception(message); }
void Near(double actual, double expected) => Check(Math.Abs(actual - expected) < 1e-9, $"Expected {expected}, got {actual}");
void Test(string name, Action run)
{
    try { Setup(name.Replace(' ', '-')); run(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; results.Add(new { name, passed = false, error = ex.Message }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
Test("Astra standard and mini exact rates", () =>
{
    var u = new TokenUsage { Input = 1000, Cached = 500, Output = 100, Reasoning = 40 };
    var p = PricingCatalog.Calculate("gpt-6-astra", u); Near(p.Credits, .2625); Near(p.Usd, .0105);
    var mini = PricingCatalog.Calculate("gpt-5.4-mini-2026-01-01", u); Near(mini.Credits, .0216125); Near(mini.Usd, .0008625);
    Check(!SessionParser.SameModel("gpt-5.4", "gpt-5.4-mini"), "mini merged with full model");
    Check(SessionParser.SameModel("gpt-6-astra-2026-09-01", "gpt-6-astra"), "dated snapshot alias");
    Check(!PricingCatalog.Calculate("gpt-6-astra-new", u).Known, "future variant must remain unknown");
});
Test("October 2 official model rate card", () =>
{
    var rates = new[] {
        (Model: "gpt-6.1-sol", Input: 50d, Cached: 2.5, Output: 250d, ApiInput: 2d, ApiCached: .1, ApiOutput: 10d),
        (Model: "gpt-6-sol", Input: 50d, Cached: 5d, Output: 250d, ApiInput: 2d, ApiCached: .2, ApiOutput: 10d),
        (Model: "gpt-6-luna", Input: 2.5, Cached: .25, Output: 12.5, ApiInput: .1, ApiCached: .01, ApiOutput: .5),
        (Model: "gpt-5.6-sol", Input: 100d, Cached: 10d, Output: 500d, ApiInput: 4d, ApiCached: .4, ApiOutput: 20d)
    };
    foreach (var rate in rates)
    {
        var input = PricingCatalog.Calculate(rate.Model, new TokenUsage { Input = 1_000_000 });
        var cached = PricingCatalog.Calculate(rate.Model, new TokenUsage { Input = 1_000_000, Cached = 1_000_000 });
        var output = PricingCatalog.Calculate(rate.Model, new TokenUsage { Output = 1_000_000 });
        Check(input.Known && cached.Known && output.Known, "published model unknown");
        Near(input.Credits, rate.Input); Near(input.Usd, rate.ApiInput);
        Near(cached.Credits, rate.Cached); Near(cached.Usd, rate.ApiCached);
        Near(output.Credits, rate.Output); Near(output.Usd, rate.ApiOutput);
    }
    Check(PricingCatalog.DisplayName("GPT-6-SOL-2026-09-22") == "GPT-6 Sol", "dated display alias");
    Check(!PricingCatalog.Calculate("gpt-6-sol-future", new TokenUsage { Input = 100 }).Known, "future model guessed");
    Check(!SessionParser.SameModel("gpt-6-sol", "gpt-5.6-sol"), "model generations merged");
});
Test("Sol and Luna context cache writes and speed", () =>
{
    // 150K ordinary + 100K cached + 50K writes + 10K output.
    var usage = new TokenUsage { Input = 300000, Cached = 100000, CacheWrite = 50000, Output = 10000 };
    foreach (var model in new[] { "gpt-6-sol", "gpt-6-luna" })
    {
        var scale = model == "gpt-6-sol" ? 1d : .05;
        var shortPrice = PricingCatalog.Calculate(model, usage, "default", 272000);
        var longPrice = PricingCatalog.Calculate(model, usage, "default", 272001);
        Near(shortPrice.Usd, .545 * scale); Near(longPrice.Usd, 1.04 * scale);
        Near(shortPrice.Credits, 13 * scale); Near(longPrice.Credits, 13 * scale);
        foreach (var tier in new[] { "fast", "priority", "FAST" })
        {
            var fast = PricingCatalog.Calculate(model, usage, tier, 272001);
            Near(fast.Usd, 2.08 * scale); Near(fast.Credits, 26 * scale);
        }
        foreach (var tier in new[] { "flex", "batch" })
        {
            var slower = PricingCatalog.Calculate(model, usage, tier, 272001);
            Near(slower.Usd, .52 * scale); Near(slower.Credits, 13 * scale);
        }
    }
    // GPT-5.6 uses the same published API rules, with its own base rate.
    foreach (var model in new[] { "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna" })
    {
        var standard = PricingCatalog.Calculate(model, new TokenUsage { Input = 100000 }, "default", 100000);
        var longFast = PricingCatalog.Calculate(model, new TokenUsage { Input = 100000 }, "fast", 300000);
        Near(longFast.Usd, standard.Usd * 4); Near(longFast.Credits, standard.Credits * 2);
    }
});
Test("New model sessions preserve generations and agent totals", () =>
{
    var path = FileFor(mainId);
    Write(path, Meta(mainId) + Context("gpt-6-sol") + Event("task_started")
        + Tokens(Usage(100000, 50000, 10000), Usage(100000, 50000, 10000))
        + Context("gpt-5.6-sol")
        + Tokens(Usage(200000, 100000, 20000), Usage(100000, 50000, 10000)));
    Write(FileFor(agentId), Meta(agentId, "subagent", mainId) + Context("gpt-6-luna", "fast")
        + Tokens(Usage(100000, 50000, 10000), Usage(100000, 50000, 10000)));
    var data = new SessionParser().Parse(path);
    Check(!data.HasUnknownPricing && data.Rows.Count == 2 && data.AgentRows.Count == 1, "new models not priced or split");
    Near(data.Rows[0].Credits, 5.25); Near(data.Rows[0].Usd, .21);
    Near(data.Rows[1].Credits, 10.5); Near(data.Rows[1].Usd, .42);
    Near(data.AgentTotalCredits, .525); Near(data.AgentTotalUsd, .021);
    Near(data.GrandTotalCredits, 16.275); Near(data.GrandTotalUsd, .651);
});
Test("Astra per-request context and tier", () =>
{
    var u = new TokenUsage { Input = 300000, Cached = 100000, Output = 10000 };
    Near(PricingCatalog.Calculate("gpt-6-astra", u, requestInput: 272000).Usd, 2.6);
    var longPrice = PricingCatalog.Calculate("gpt-6-astra", u, requestInput: 272001);
    Near(longPrice.Usd, 4.95); Near(longPrice.Credits, 65);
    var fast = PricingCatalog.Calculate("gpt-6-astra", u, "priority", 272001); Near(fast.Credits, 130); Near(fast.Usd, 9.9);
    Near(PricingCatalog.Calculate("gpt-6-astra", u, "flex", 272001).Usd, 2.475);
});
Test("Cache writes are not double counted", () =>
{
    var u = new TokenUsage { Input = 1000, Cached = 300, CacheWrite = 200, Output = 100 };
    var p = PricingCatalog.Calculate("gpt-6-astra", u); Near(p.Credits, .3075); Near(p.Usd, .0128);
});
Test("Sol 6.1 lower cache rates and speed boundaries", () =>
{
    var usage = new TokenUsage { Input = 300000, Cached = 100000, CacheWrite = 50000, Output = 10000 };
    var standard = PricingCatalog.Calculate("gpt-6.1-sol", usage, "default", 272000);
    var longPrice = PricingCatalog.Calculate("gpt-6.1-sol", usage, "default", 272001);
    Near(standard.Credits, 12.75); Near(standard.Usd, .535);
    Near(longPrice.Credits, 12.75); Near(longPrice.Usd, 1.02);
    var fast = PricingCatalog.Calculate("gpt-6.1-sol-2026-10-01", usage, "priority", 272001);
    Near(fast.Credits, 25.5); Near(fast.Usd, 2.04);
    foreach (var tier in new[] { "flex", "batch" })
    {
        var price = PricingCatalog.Calculate("gpt-6.1-sol", usage, tier, 272001);
        Near(price.Credits, 12.75); Near(price.Usd, .51);
    }
    Check(!SessionParser.SameModel("gpt-6.1-sol", "gpt-6-sol"), "6.1 merged into 6");
    Check(!PricingCatalog.Calculate("gpt-6.1-sol-next", usage).Known, "future model guessed");
    Check(!PricingCatalog.Calculate("gpt-6.1-sol", usage, "ultrafast").Known, "unpublished Ultrafast appears priced");
});
Test("Astra Ultrafast composes with context and writes", () =>
{
    var usage = new TokenUsage { Input = 300000, Cached = 100000, CacheWrite = 50000, Output = 10000 };
    var standard = PricingCatalog.Calculate("gpt-6-astra", usage, "default", 272001);
    var ultra = PricingCatalog.Calculate("gpt-6-astra", usage, "ULTRAFAST", 272001);
    Check(ultra.Known, "published Ultrafast unknown");
    Near(ultra.Credits, 390); Near(ultra.Usd, 31.2);
    Near(ultra.Credits, standard.Credits * 6); Near(ultra.Usd, standard.Usd * 6);
});
Test("Sol 6.1 model changes and nested tiers", () =>
{
    var path = FileFor(mainId);
    Write(path, Meta(mainId) + Context("gpt-6-sol") + Event("task_started")
        + Tokens(Usage(100000, 50000, 10000), Usage(100000, 50000, 10000))
        + Record("event_msg", new { type = "thread_settings_applied", thread_settings = new { model = "gpt-6.1-sol", service_tier = "fast" } })
        + Tokens(Usage(200000, 100000, 20000), Usage(100000, 50000, 10000)));
    Write(FileFor(agentId), Meta(agentId, "subagent", mainId) + Context("gpt-6.1-sol")
        + Tokens(Usage(100000, 50000, 10000), Usage(100000, 50000, 10000)));
    var data = new SessionParser().Parse(path);
    Check(data.RequestedModel == "gpt-6.1-sol" && data.Rows.Count == 2 && !data.HasUnknownPricing, "new model not detected");
    Check(data.Rows[1].Model == "GPT-6.1 Sol", "new model label missing");
    Near(data.Rows[0].Credits, 5.25); Near(data.Rows[1].Credits, 10.25);
    Near(data.AgentTotalCredits, 5.125); Near(data.GrandTotalCredits, 20.625);
    Near(data.GrandTotalUsd, .825);
});
Test("Duplicate totals limits-only events and aborted turns", () =>
{
    var path = FileFor(mainId); var u = Usage(1000, 500);
    var limits = new { limit_id = "codex", primary = new { used_percent = 42.5, window_minutes = 300, resets_at = 1893456000 }, secondary = new { used_percent = 63, window_minutes = 10080, resets_at = 1893542400 } };
    Write(path, Meta(mainId) + Context("gpt-6-astra") + Event("task_started") + Record("event_msg", new { type = "user_message", message = "检查优化模型" }) + Tokens(u, u) + Tokens(u, u)
        + Record("event_msg", new { type = "token_count", info = (object?)null, rate_limits = limits }) + Event("turn_aborted"));
    var parser = new SessionParser(); var data = parser.Parse(path);
    Check(data.Rows.Count == 1 && data.Rows[0].Status == "STOPPED", "abort status");
    Check(data.Totals.Input == 1000 && data.Totals.Output == 100 && data.Rows[0].Reasoning == 40, "duplicate tokens or reasoning double count");
    Check(data.Title == "检查优化模型", "unicode title fallback");
    Near(data.TotalCredits, .2625); Near(data.FiveHour!.Value, 42.5); Near(parser.GetLatestAccountUsage()!.Weekly!.Value, 63);
    Check(data.LastRequestInput == 1000 && data.ContextWindow == 258400, "context snapshot");
    var read = parser.BytesRead; parser.Parse(path); Check(parser.BytesRead == read, "unchanged file reread");
});
Test("Partial UTF8 append and valid tail without newline", () =>
{
    var path = FileFor(mainId); Write(path, Meta(mainId) + Context("gpt-6-astra") + Event("task_started"));
    var parser = new SessionParser(); parser.Parse(path);
    var prompt = Encoding.UTF8.GetBytes(Record("event_msg", new { type = "user_message", message = "模型优化😀" }));
    var split = Array.FindIndex(prompt, b => b >= 128) + 1; // Split inside a UTF-8 character.
    using (var f = new FileStream(path, FileMode.Append)) f.Write(prompt.AsSpan(0, split));
    Check(parser.Parse(path).MalformedLines == 0, "partial record counted as corrupt");
    using (var f = new FileStream(path, FileMode.Append)) f.Write(prompt.AsSpan(split));
    Append(path, Tokens(Usage(1000), Usage(1000)).TrimEnd('\n'));
    var data = parser.Parse(path); Check(data.Title == "模型优化😀" && data.Totals.Input == 1000, "partial recovery");
    Append(path, "\n" + Tokens(Usage(1500, output: 150), Usage(500, output: 50)) + Event("task_complete"));
    data = parser.Parse(path); Check(data.Totals.Input == 1500 && data.Totals.Output == 150 && data.Rows[0].Status == "DONE", "tail counted twice");
});
Test("Model switch compaction reset and malformed records", () =>
{
    var path = FileFor(mainId); Write(path, Meta(mainId) + Context("gpt-5.4") + Event("task_started") + Tokens(Usage(1000), Usage(1000)));
    var parser = new SessionParser(); var before = parser.Parse(path);
    Append(path, Context("gpt-5.4-mini") + Tokens(Usage(1500, output: 150), Usage(500, output: 50)) + Record("compacted", new { }) + "{broken\n"
        + "null\n" + Record("event_msg", new { type = "token_count", info = new { model_context_window = "invalid" } })
        + Tokens(Usage(200, output: 20), Usage(200, output: 20)) + Event("turn_aborted"));
    var data = parser.Parse(path); var fresh = new SessionParser().Parse(path);
    Check(data.Rows.Count == 2 && data.Rows[1].ModelId == "gpt-5.4-mini" && data.Rows[1].Status == "STOPPED", "model segments");
    Check(data.Totals.Input == 1700 && data.CompactionCount == 1 && data.MalformedLines == 1, "compaction reset or corrupt record");
    Near(data.TotalCredits, fresh.TotalCredits); Check(data.Totals.SameCounters(fresh.Totals), "incremental differs from fresh");
    Check(before.Rows.Count == 1 && before.Rows[0].IsLive && before.Totals.Input == 1000, "prior snapshot mutated");
});
Test("Legacy totals fallback and resumed usage", () =>
{
    var path = FileFor(mainId); Write(path, Meta(mainId) + Context("gpt-6-astra") + Tokens(Usage(1000), null) + Tokens(Usage(1500, output: 200), null));
    var d = new SessionParser().Parse(path); Check(d.Totals.Input == 1500 && d.Totals.Output == 200 && d.Rows.Count == 1, "legacy fallback");
    Check(d.Rows[0].IsLive, "resumed usage status");
});
Test("Nested agents unknown models and empty child discovery", () =>
{
    var path = FileFor(mainId); Write(path, Meta(mainId) + Context("gpt-6-astra") + Event("task_started") + Tokens(Usage(1000), Usage(1000)));
    Write(FileFor(agentId), Meta(agentId, "subagent", mainId) + Context("gpt-5.4-mini") + Tokens(Usage(500), Usage(500)));
    Write(FileFor(nestedId), Meta(nestedId, "subagent", agentId) + Context("future-model") + Tokens(Usage(200), Usage(200)));
    var guardian = "44444444-4444-4444-8444-444444444444";
    Write(FileFor(guardian), Meta(guardian, "guardian_review", mainId) + Context("codex-auto-review") + Tokens(Usage(9000), Usage(9000)));
    var empty = "55555555-5555-4555-8555-555555555555"; Write(FileFor(empty), Meta(empty, "subagent", mainId));
    var parser = new SessionParser(); var d = parser.Parse(path);
    Check(d.AgentRows.Count == 2 && d.AgentSessionCount == 2 && d.AgentPaths.Count == 3, "agent discovery or guardian exclusion");
    Check(d.HasUnknownPricing && d.AgentRows.Single(r => r.ModelId == "future-model").CreditsText == "Unknown", "unknown price appears free");
    Append(FileFor(empty), Context("gpt-5.6-luna") + Tokens(Usage(100), Usage(100)));
    Check(parser.Parse(path).AgentSessionCount == 3, "first usage of empty child missed");
});
Test("Title updates filename identity and truncation", () =>
{
    var path = FileFor(mainId); Write(path, Meta(agentId) + Context("gpt-6-astra") + Tokens(Usage(1000), Usage(1000)));
    var index = Path.Combine(Directory.GetParent(Path.GetDirectoryName(path)!)!.FullName, "session_index.jsonl");
    Write(index, JsonSerializer.Serialize(new { id = mainId, thread_name = "优化分析" }) + "\n");
    var p = new SessionParser(); Check(p.Parse(path).SessionId == mainId && p.GetRecentSessionChoices()[0].Label == "优化分析", "fork identity/title");
    Append(index, JsonSerializer.Serialize(new { id = mainId, thread_name = "优化分析 · 新标题" }) + "\n");
    Check(p.Parse(path).Title == "优化分析 · 新标题", "title cache stale");
    Write(path, Meta(mainId) + Context("gpt-5.6-luna")); Check(p.Parse(path).Rows.Count == 0, "truncate reset");
});
Test("Global snapshot bucket selection", () =>
{
    var limits = new { limit_id = "codex", primary = new { used_percent = 17, window_minutes = 300, resets_at = 1893456000 } };
    Write(FileFor(mainId), Meta(mainId) + Record("event_msg", new { type = "token_count", info = (object?)null, rate_limits = limits }));
    at = at.AddMinutes(1);
    Write(FileFor(agentId), Meta(agentId, "subagent", mainId) + Record("event_msg", new { type = "token_count", info = (object?)null, rate_limits = new { limit_id = "codex", primary = new { used_percent = 31, window_minutes = 300, resets_at = 1893456000 } } })
        + Record("event_msg", new { type = "token_count", info = (object?)null, rate_limits = new { limit_id = "other", primary = new { used_percent = 99, window_minutes = 300 } } }));
    Near(new SessionParser().GetLatestAccountUsage()!.FiveHour!.Value, 31);
});
Test("Settings roundtrip and corrupt fallback", () =>
{
    var path = Path.Combine(root, "settings.json"); Environment.SetEnvironmentVariable("CODEX_TOKEN_MONITOR_SETTINGS_PATH", path);
    new MonitorSettings { Topmost = true, ShowRemainingQuota = true, Left = 120, Top = 240 }.Save(); var loaded = MonitorSettings.Load();
    Check(loaded.Topmost && loaded.ShowRemainingQuota && loaded.Left == 120 && loaded.Top == 240, "settings roundtrip");
    Write(path, "broken"); Check(MonitorSettings.Load() is not null, "corrupt settings fallback");
});
Test("Large log append reads only new bytes", () =>
{
    var path = FileFor(mainId); Write(path, Meta(mainId) + Context("gpt-6-astra") + string.Concat(Enumerable.Repeat(Record("response_item", new { role = "assistant", text = new string('x', 200) }), 16000)) + Tokens(Usage(1000), Usage(1000)));
    var parser = new SessionParser(); parser.Parse(path); var read = parser.BytesRead;
    var tail = Tokens(Usage(1500, output: 150), Usage(500, output: 50)); Append(path, tail);
    var d = parser.Parse(path); Check(d.Totals.Input == 1500 && parser.BytesRead - read == Encoding.UTF8.GetByteCount(tail), "full log reread on append");
});
Test("Requested model alone never verifies returned model", () =>
{
    var path = FileFor(mainId);
    Write(path, Meta(mainId) + Event("task_started") + Context("gpt-6-astra") + Tokens(Usage(1000), Usage(1000)));
    var parser = new SessionParser(); var d = parser.Parse(path);
    Check(d.RequestedModel == "gpt-6-astra" && d.ModelEvidence is null && d.ModelCheck == "未验证", "request falsely verified");
    Append(path, Record("event_msg", new { type = "thread_settings_applied", thread_settings = new { model = "gpt-5.6-sol" } }));
    d = parser.Parse(path);
    Check(d.RequestedModel == "gpt-5.6-sol" && d.ModelCheck == "未验证", "nested thread settings ignored");
});
Test("Reroute evidence is isolated by turn and requested model", () =>
{
    var path = FileFor(mainId);
    var reroute = Record("event_msg", new { type = "model_reroute", from_model = "gpt-6-astra", to_model = "gpt-5.6-sol", turn_id = "turn-1" });
    Write(path, Meta(mainId) + Event("task_started") + Context("gpt-6-astra") + Tokens(Usage(1000), Usage(1000)) + reroute);
    var parser = new SessionParser(); var d = parser.Parse(path);
    Check(d.ModelCheck == "名称不同" && d.ModelEvidence?.ReturnedModel == "gpt-5.6-sol", "reroute missing");
    Check(d.Rows[0].ModelId == "gpt-6-astra", "verification must not silently change billing attribution");
    Append(path, Record("event_msg", new { type = "task_started", turn_id = "turn-2" }) + reroute);
    d = parser.Parse(path); Check(d.ModelCheck == "未验证", "old turn leaked");
    Append(path, Record("event_msg", new { type = "model_reroute", from_model = "gpt-6-astra", to_model = "gpt-5.6-sol", turn_id = "turn-2" }));
    Check(parser.Parse(path).ModelCheck == "名称不同", "new turn evidence missing");
    Append(path, Context("gpt-5.6-sol"));
    Check(parser.Parse(path).ModelCheck == "未验证", "changed request reused old return");
});
Test("Runtime evidence rejects tool text and foreign turns", () =>
{
    const string target = "codex_core::session";
    const string match = "session_loop{thread_id=x}:turn{turn.id=turn-1 model=gpt-6-astra}: server reported model gpt-6-astra (matches requested model)";
    var evidence = ModelResponseLogReader.ParseReport(target, match, "turn-1", "gpt-6-astra", at);
    Check(evidence?.ReturnedModel == "gpt-6-astra", "valid equality evidence ignored");
    Check(ModelResponseLogReader.ParseReport(target, match, "turn-2", "gpt-6-astra", at) is null, "foreign turn accepted");
    Check(ModelResponseLogReader.ParseReport(target, match, "turn-1", "gpt-5.6-sol", at) is null, "wrong request accepted");
    Check(ModelResponseLogReader.ParseReport("codex_core::tools", match, "turn-1", "gpt-6-astra", at) is null, "wrong logger accepted");
    Check(ModelResponseLogReader.ParseReport(target, "ToolCall: " + match, "turn-1", "gpt-6-astra", at) is null, "quoted tool text accepted");
    Check(ModelResponseLogReader.ParseReport(target, "server reported model gpt-6-astra (matches requested model)", "turn-1", "gpt-6-astra", at) is null, "unscoped report accepted");
    var different = ModelResponseLogReader.ParseReport(target, match.Replace("gpt-6-astra (matches requested model)", "gpt-6-astra-2026-09-01 while requested model was gpt-6-astra"), "turn-1", "gpt-6-astra", at);
    Check(new SessionSummary { RequestedModel = "gpt-6-astra", ModelEvidence = different }.ModelCheck == "名称不同", "dated names falsely declared identical");
});
if (OperatingSystem.IsWindows()) Test("Native SQLite reads are scoped and read-only", () =>
{
    var dbRoot = Directory.GetParent(Environment.GetEnvironmentVariable("CODEX_TOKEN_MONITOR_SESSION_ROOT")!)!.FullName;
    var dbPath = Path.Combine(dbRoot, "logs_2.sqlite");
    const string message = "turn{turn.id=turn-1 model=gpt-6-astra}: server reported model gpt-6-astra (matches requested model)";
    var stamp = new DateTimeOffset(at).ToUnixTimeSeconds();
    NativeModelFixture.Create(dbPath, $"CREATE TABLE logs(id INTEGER PRIMARY KEY,ts INTEGER,ts_nanos INTEGER,target TEXT,thread_id TEXT,feedback_log_body TEXT);"
        + $"INSERT INTO logs VALUES(1,{stamp},0,'codex_core::session','{mainId}','{message}');"
        + $"INSERT INTO logs VALUES(2,{stamp + 1},0,'codex_core::session','{agentId}','{message.Replace("gpt-6-astra (matches requested model)", "gpt-5.6-sol while requested model was gpt-6-astra")}');");
    var before = File.ReadAllBytes(dbPath);
    var evidence = ModelResponseLogReader.ReadLatest(dbRoot, mainId, "turn-1", "gpt-6-astra", at);
    Check(evidence?.ReturnedModel == "gpt-6-astra", "native reader or thread isolation failed");
    Check(ModelResponseLogReader.ReadLatest(dbRoot, mainId, "turn-2", "gpt-6-astra", at) is null, "wrong turn read");
    Check(ModelResponseLogReader.ReadLatest(dbRoot, mainId, "turn-1", "gpt-6-astra", at.AddSeconds(5)) is null, "stale evidence read");
    Check(before.SequenceEqual(File.ReadAllBytes(dbPath)), "database modified");
});
Test("Quota display modes and stale snapshots", QuotaDisplayChecks.Run);
Test("Account snapshots survive large appends and replacements", AccountSnapshotChecks.Run);
Test("Paths remain portable and Codex data remains read-only", PathResolutionChecks.Run);
Test("Startup path overrides have explicit process-only precedence", PathResolutionChecks.RunStartupOptions);
// Durable, synthetic preview data. No real task text is included in exported artifacts.
Setup("preview");
var previewPath = FileFor(mainId);
var previewLimits = new { limit_id = "codex", primary = new { used_percent = 42.5, window_minutes = 300, resets_at = 1893456000 }, secondary = new { used_percent = 63, window_minutes = 10080, resets_at = 1893542400 } };
Write(previewPath, Meta(mainId) + Context("gpt-6-astra") + Event("task_started") + Record("event_msg", new { type = "user_message", message = "Compare optimization strategies and validate the results" })
    + Tokens(Usage(210825, 160000, 14200, 6200), Usage(210825, 160000, 14200, 6200), previewLimits) + Event("task_complete")
    + Context("gpt-5.4-mini") + Event("task_started") + Tokens(Usage(220825, 165000, 15200), Usage(10000, 5000, 1000)) + Event("task_complete")
    + Context("gpt-6-astra-2026-09-01") + Event("task_started") + Tokens(Usage(420825, 300000, 30000), Usage(200000, 135000, 14800)));
Write(FileFor(agentId), Meta(agentId, "subagent", mainId) + Context("gpt-5.6-sol") + Tokens(Usage(64000, 44000, 4300), Usage(64000, 44000, 4300)) + Event("task_complete"));
Write(Path.Combine(root, "preview", "session_index.jsonl"), JsonSerializer.Serialize(new { id = mainId, thread_name = "Optimization study · synthetic demo" }) + "\n");
File.WriteAllText(Path.Combine(root, "test-results.json"), JsonSerializer.Serialize(new { passed = results.Count - failures, failed = failures, tests = results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count - failures}/{results.Count} passed. Artifacts: {root}");
Environment.ExitCode = failures == 0 ? 0 : 1;

static class NativeModelFixture
{
    public static void Create(string path, string sql)
    {
        var code = sqlite3_open_v2(Encoding.UTF8.GetBytes(path + '\0'), out var db, 6, IntPtr.Zero);
        try { if (code != 0 || sqlite3_exec(db, Encoding.UTF8.GetBytes(sql + '\0'), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0) throw new Exception("SQLite fixture failed"); }
        finally { if (db != IntPtr.Zero) sqlite3_close(db); }
    }
    [System.Runtime.InteropServices.DllImport("winsqlite3", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] private static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
    [System.Runtime.InteropServices.DllImport("winsqlite3", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, IntPtr error);
    [System.Runtime.InteropServices.DllImport("winsqlite3", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
}

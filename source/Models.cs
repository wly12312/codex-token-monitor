namespace CodexTokenMonitor;

public sealed class TokenUsage
{
    public long Input { get; set; }
    public long Cached { get; set; }
    public long CacheWrite { get; set; }
    public long Output { get; set; }
    public long Reasoning { get; set; }
    public long Total { get; set; }

    public TokenUsage Clone() => (TokenUsage)MemberwiseClone();

    public void Add(TokenUsage other)
    {
        Input += other.Input;
        Cached += other.Cached;
        CacheWrite += other.CacheWrite;
        Output += other.Output;
        Reasoning += other.Reasoning;
        Total += other.Total;
    }

    public bool SameCounters(TokenUsage other) =>
        Input == other.Input && Cached == other.Cached && CacheWrite == other.CacheWrite &&
        Output == other.Output && Reasoning == other.Reasoning && Total == other.Total;

    public static TokenUsage Delta(TokenUsage current, TokenUsage previous) => new()
    {
        Input = SafeDelta(current.Input, previous.Input),
        Cached = SafeDelta(current.Cached, previous.Cached),
        CacheWrite = SafeDelta(current.CacheWrite, previous.CacheWrite),
        Output = SafeDelta(current.Output, previous.Output),
        Reasoning = SafeDelta(current.Reasoning, previous.Reasoning),
        Total = SafeDelta(current.Total, previous.Total)
    };

    private static long SafeDelta(long current, long previous) => current >= previous ? current - previous : current;
}

public sealed class TurnRow
{
    public int Number { get; set; }
    public string TurnId { get; set; } = "";
    public bool IsLive { get; set; }
    public bool IsAborted { get; set; }
    public string Status => IsLive ? "LIVE" : IsAborted ? "STOPPED" : "DONE";
    public DateTime Started { get; set; }
    public string Time => Started.ToLocalTime().ToString("HH:mm");
    public string ModelId { get; set; } = "unknown";
    public string Model { get; set; } = "Unknown";
    public bool IsSubagent { get; set; }
    public string SourceSessionId { get; set; } = "";
    public string ParentSessionId { get; set; } = "";
    public long Input { get; set; }
    public long Cached { get; set; }
    public long CacheWrite { get; set; }
    public long Uncached { get; set; }
    public long Output { get; set; }
    public long Reasoning { get; set; }
    public long Total { get; set; }
    public double Credits { get; set; }
    public double Usd { get; set; }
    public bool PricingKnown { get; set; } = true;
    public string PricingNote { get; set; } = "Standard reference estimate";
    public double? FiveHour { get; set; }
    public double? Weekly { get; set; }
    public string InputText => TokenFormatter.Compact(Input);
    public string CachedText => TokenFormatter.Compact(Cached);
    public string UncachedText => TokenFormatter.Compact(Uncached);
    public string OutputText => TokenFormatter.Compact(Output);
    public string ReasoningText => TokenFormatter.Compact(Reasoning);
    public string TotalText => TokenFormatter.Compact(Total);
    public string CreditsText => PricingKnown ? Credits.ToString("N3") : "Unknown";
    public string UsdText => PricingKnown ? "$" + Usd.ToString("N4") : "Unknown";
    public TurnRow Clone() => (TurnRow)MemberwiseClone();
    public string FiveHourText => FiveHour.HasValue ? $"{FiveHour:0.#}%" : "--";
    public string WeeklyText => Weekly.HasValue ? $"{Weekly:0.#}%" : "--";
}

public static class TokenFormatter
{
    public static string Compact(long value)
    {
        if (Math.Abs(value) >= 1_000_000)
            return (value / 1_000_000d).ToString(value >= 10_000_000 ? "0.0'M'" : "0.00'M'");
        if (Math.Abs(value) >= 1_000)
            return (value / 1_000d).ToString(value >= 100_000 ? "0.0'K'" : "0.00'K'");
        return value.ToString("N0");
    }
}

public sealed class SessionSummary
{
    public string Path { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Title { get; set; } = "";
    public string CurrentModel { get; set; } = "unknown";
    public string RequestedModel { get; set; } = "";
    public string LatestTurnId { get; set; } = "";
    public DateTime ModelRequestAt { get; set; }
    public ModelResponseEvidence? ModelEvidence { get; set; }
    public string ModelCheck => ModelEvidence is null || RequestedModel.Length == 0 ? "未验证"
        : string.Equals(RequestedModel, ModelEvidence.ReturnedModel, StringComparison.OrdinalIgnoreCase) ? "名称一致" : "名称不同";
    public string ModelCheckDetail => $"主任务所选：{(RequestedModel.Length == 0 ? "未记录" : RequestedModel)}\n服务端返回：{ModelEvidence?.ReturnedModel ?? "未记录"}\n"
        + (ModelEvidence is null ? "当前轮次没有可核验的服务端模型记录。" : $"来源：{ModelEvidence.Source}\n记录时间：{ModelEvidence.ObservedAt.ToLocalTime():MM-dd HH:mm:ss}\n仅表示当前轮次最近一条可用回报。")
        + "\n模型名称校验不能判断回答质量或账户是否被降级。";
    public List<TurnRow> Rows { get; set; } = [];
    public List<TurnRow> AgentRows { get; set; } = [];
    public int AgentSessionCount { get; set; }
    public TokenUsage Totals { get; set; } = new();
    public double TotalCredits { get; set; }
    public double TotalUsd { get; set; }
    public double AgentTotalCredits => AgentRows.Sum(row => row.Credits);
    public double AgentTotalUsd => AgentRows.Sum(row => row.Usd);
    public double GrandTotalCredits => TotalCredits + AgentTotalCredits;
    public double GrandTotalUsd => TotalUsd + AgentTotalUsd;
    public double? FiveHour { get; set; }
    public double? Weekly { get; set; }
    public long? ContextWindow { get; set; }
    public long? LastRequestInput { get; set; }
    public DateTime? ContextObservedAt { get; set; }
    public int CompactionCount { get; set; }
    public int MalformedLines { get; set; }
    public List<string> AgentPaths { get; set; } = [];
    public AccountUsageSnapshot? AccountUsage { get; set; }
    public bool HasUnknownPricing => Rows.Concat(AgentRows).Any(row => !row.PricingKnown);
    public DateTime LastWriteTime { get; set; }
}

public sealed record ModelResponseEvidence(string RequestedModel, string ReturnedModel, string TurnId, DateTime ObservedAt, string Source);

public sealed class UsageScopeChoice
{
    public string Label { get; init; } = "";
    public string ModelId { get; init; } = "unknown";
    public bool IsAgent { get; init; }
    public int AgentCount { get; init; }
    public string Key => (IsAgent ? "agent:" : "main:") + ModelId.ToLowerInvariant();
}

public sealed class SessionChoice
{
    public string Label { get; init; } = "";
    public string DisplayLabel => string.Join(" ", Label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    public string Path { get; init; } = "";
    public string SessionId { get; init; } = "";
    public DateTime Updated { get; init; }
}

internal sealed record ModelRate(string Label, double InputCredits, double CachedCredits, double OutputCredits,
    double InputUsd, double CachedUsd, double OutputUsd);

public sealed class AccountUsageSnapshot
{
    public double? FiveHour { get; set; }
    public double? Weekly { get; set; }
    public DateTimeOffset? FiveHourResetsAt { get; set; }
    public DateTimeOffset? WeeklyResetsAt { get; set; }
    public DateTime ObservedAt { get; set; }
    public string? Plan { get; set; }
}

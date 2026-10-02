using System;

namespace CodexTokenMonitor;

public sealed record QuotaPresentation(
    double? UsedPercent,
    double? DisplayPercent,
    bool IsExpired,
    bool IsStale);

public static class QuotaDisplay
{
    public static QuotaPresentation Create(
        double? usedPercent,
        DateTimeOffset? resetsAt,
        DateTime? observedAtUtc,
        bool showRemaining,
        DateTimeOffset now)
    {
        // A passed reset invalidates the snapshot; only a new observation can confirm zero use.
        if (resetsAt is { } reset && reset <= now)
            return new(null, null, true, false);

        double? used = usedPercent is { } value && double.IsFinite(value)
            ? Math.Clamp(value, 0, 100)
            : null;
        var display = used is { } percent ? showRemaining ? 100 - percent : percent : (double?)null;
        var stale = false;
        if (used.HasValue && observedAtUtc is { } observed)
        {
            // Log timestamps without a DateTime kind are still UTC, as the parameter promises.
            var utc = observed.Kind == DateTimeKind.Local
                ? observed.ToUniversalTime()
                : DateTime.SpecifyKind(observed, DateTimeKind.Utc);
            stale = now.UtcDateTime - utc > TimeSpan.FromMinutes(15);
        }

        return new(used, display, false, stale);
    }
}

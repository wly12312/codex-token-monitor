using System;
using CodexTokenMonitor;

public static class QuotaDisplayChecks
{
    public static void Run()
    {
        var now = new DateTimeOffset(2026, 9, 22, 8, 30, 0, TimeSpan.Zero);
        QuotaPresentation Present(double? used, bool remaining = false, DateTimeOffset? reset = null, DateTime? observed = null)
            => QuotaDisplay.Create(used, reset, observed ?? now.UtcDateTime, remaining, now);

        var used = Present(31.5);
        Check(used.UsedPercent == 31.5 && used.DisplayPercent == 31.5, "Used percentage must be preserved.");
        Check(!used.IsExpired && !used.IsStale, "A fresh snapshot without an expiry must remain valid.");
        var remaining = Present(31.5, remaining: true);
        Check(remaining.UsedPercent == 31.5 && remaining.DisplayPercent == 68.5, "Remaining mode must retain original used percentage.");
        Check(Present(-1).UsedPercent == 0 && Present(101).UsedPercent == 100, "Finite percentages must be clamped.");
        Check(Present(-1, true).DisplayPercent == 100 && Present(101, true).DisplayPercent == 0, "Remaining must use the clamped value.");
        Check(Present(0, true).DisplayPercent == 100 && Present(100, true).DisplayPercent == 0, "Percentage endpoints must be exact.");

        foreach (var unknown in new double?[] { null, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            foreach (var showRemaining in new[] { false, true })
            {
                var result = Present(unknown, showRemaining, observed: now.UtcDateTime.AddHours(-1));
                Check(result.UsedPercent is null && result.DisplayPercent is null && !result.IsStale,
                    "Unknown percentages must not turn into free or fully available quota.");
            }
        }

        foreach (var reset in new[] { now, now.AddTicks(-1), now.AddDays(-1) })
        {
            foreach (var showRemaining in new[] { false, true })
            {
                var expired = Present(40, showRemaining, reset, now.UtcDateTime.AddHours(-1));
                Check(expired.UsedPercent is null && expired.DisplayPercent is null && expired.IsExpired
                    && !expired.IsStale, "Expired snapshots must wait for new data.");
            }
        }
        Check(Present(null, reset: now).IsExpired, "An expired reset must be recognized even without a percentage.");

        var valid = Present(40, reset: now.AddTicks(1));
        Check(!valid.IsExpired && valid.DisplayPercent == 40, "A snapshot remains valid until its expiry.");
        Check(!Present(40, reset: now.AddHours(2).ToOffset(TimeSpan.FromHours(8))).IsExpired,
            "Expiry timestamps with another offset must represent the same instant.");

        Check(!Present(40, observed: now.UtcDateTime.AddMinutes(-15)).IsStale, "Exactly 15 minutes is not stale.");
        Check(Present(40, observed: now.UtcDateTime.AddMinutes(-15).AddTicks(-1)).IsStale, "Older than 15 minutes is stale.");
        Check(!Present(40, observed: now.UtcDateTime.AddHours(1)).IsStale, "Future observations must not be stale.");
        Check(Present(0, observed: now.UtcDateTime.AddHours(-1)).IsStale, "Zero is a valid percentage for freshness checks.");
        Check(Present(40, observed: DateTime.SpecifyKind(now.UtcDateTime.AddHours(-1), DateTimeKind.Unspecified)).IsStale,
            "Unspecified observation timestamps must be interpreted as UTC.");
        Check(Present(40, observed: now.UtcDateTime.AddHours(-1).ToLocalTime()).IsStale,
            "Local observation timestamps must be converted to UTC.");
        Check(!QuotaDisplay.Create(40, null, null, false, now).IsStale, "Missing observation time must not invent an age.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

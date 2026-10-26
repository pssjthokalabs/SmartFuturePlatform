using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartFuture.Application.Jobs.Import;

/// <summary>
/// One daily slot: the scheduled import for one local calendar date.
/// <see cref="StartUtc"/> is the scheduled time; the slot owns every
/// moment up to <see cref="NextStartUtc"/>, so an API that was asleep at
/// 05:00 still runs that day's import as soon as it wakes.
/// </summary>
public sealed record JobImportSlot(DateOnly Date, DateTime StartUtc, DateTime NextStartUtc);

/// <summary>
/// Pure policy for the once-daily scheduled import: when a slot starts,
/// how retries back off, and the deterministic id each attempt claims.
/// No I/O, so every rule here is unit-tested directly.
/// </summary>
public static class JobImportDailySchedule
{
    /// <summary>Source name written on the summary row of each attempt.</summary>
    public const string BatchRunName = "Daily scheduled import";

    /// <summary>The brief allows at most 5 retries after the first attempt.</summary>
    public const int MaxRetryAttemptsCeiling = 5;

    /// <summary>
    /// How long one attempt may run before it stops itself and leaves
    /// the remaining sources to the next retry. Kept well inside
    /// <see cref="HeartbeatStaleAfter"/>' reach: a live attempt writes a
    /// heartbeat after every source, so it can never look abandoned.
    /// </summary>
    public static readonly TimeSpan AttemptBudget = TimeSpan.FromHours(2);

    /// <summary>
    /// An attempt whose row has not been touched for this long is treated
    /// as interrupted (the API process died mid-run) and marked failed so
    /// the next retry can claim the slot. One source is capped at
    /// <see cref="JobImportService.BackgroundRunLimit"/>, and the attempt
    /// heartbeats after each source, so a live attempt is never older.
    /// </summary>
    public static readonly TimeSpan HeartbeatStaleAfter = JobImportService.BackgroundRunLimit + TimeSpan.FromMinutes(15);

    /// <summary>
    /// A source whose own interval is LONGER than a day (e.g. weekly) is
    /// included in a daily run only when it falls due within this window
    /// after the scheduled time — its existing interval rule is kept.
    /// </summary>
    public static readonly TimeSpan DueLookahead = TimeSpan.FromHours(12);

    private const int MinutesPerDay = 1440;
    private const string DefaultTimeZoneId = "Africa/Johannesburg";
    private static readonly TimeOnly DefaultRunTime = new(5, 0);

    public static TimeOnly ResolveRunTime(string? value)
        => TimeOnly.TryParseExact(value?.Trim(), new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : DefaultRunTime;

    /// <summary>
    /// The configured zone, else South Africa. Tries the IANA id first
    /// (ICU), then the Windows id, then a fixed UTC+2 zone — so a server
    /// without IANA data can never stop the importer. SAST has no DST,
    /// which makes the fixed fallback exact.
    /// </summary>
    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        foreach (var candidate in new[] { id, DefaultTimeZoneId, "South Africa Standard Time" })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(candidate.Trim());
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Try the next candidate.
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(DefaultTimeZoneId, TimeSpan.FromHours(2), "South Africa Standard Time", "South Africa Standard Time");
    }

    /// <summary>The most recent slot whose scheduled time is at or before <paramref name="nowUtc"/>.</summary>
    public static JobImportSlot CurrentSlot(DateTime nowUtc, JobImportScheduleSettings settings)
    {
        var zone = ResolveTimeZone(settings.TimeZone);
        var runTime = ResolveRunTime(settings.DailyRunTime);
        var utc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
        var date = DateOnly.FromDateTime(localNow);
        if (TimeOnly.FromDateTime(localNow) < runTime) date = date.AddDays(-1);

        var start = ToUtc(date, runTime, zone);
        // Only reachable around a DST jump in a zone that has one.
        if (start > utc)
        {
            date = date.AddDays(-1);
            start = ToUtc(date, runTime, zone);
        }

        return new JobImportSlot(date, start, ToUtc(date.AddDays(1), runTime, zone));
    }

    public static int MaxRetries(JobImportScheduleSettings settings) => Math.Clamp(settings.MaxRetryAttempts, 0, MaxRetryAttemptsCeiling);

    /// <summary>
    /// Delay before retry <paramref name="retryNumber"/> (1-based):
    /// base, 2×base, 4×base … capped. Defaults give 5, 10, 20, 40, 60 min.
    /// </summary>
    public static TimeSpan RetryDelay(int retryNumber, JobImportScheduleSettings settings)
    {
        var baseMinutes = Math.Clamp(settings.RetryBaseDelayMinutes, 1, 60);
        var maxMinutes = Math.Clamp(settings.RetryMaxDelayMinutes, baseMinutes, 240);
        var minutes = baseMinutes * Math.Pow(2, Math.Clamp(retryNumber - 1, 0, 16));
        return TimeSpan.FromMinutes(Math.Min(minutes, maxMinutes));
    }

    /// <summary>
    /// Primary key of attempt <paramref name="attempt"/> for the slot on
    /// <paramref name="slotDate"/>. The SAME on every API instance, which
    /// is what makes the claim exclusive: two instances inserting attempt
    /// 1 for the same day collide on the JobImportRuns primary key and
    /// only one insert succeeds. No lock table, no migration.
    /// </summary>
    public static Guid AttemptId(DateOnly slotDate, int attempt)
    {
        var key = $"smartfuture:job-import:daily:{slotDate:yyyy-MM-dd}:attempt:{attempt}";
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
    }

    /// <summary>
    /// Whether a scheduled source belongs in the daily run for this slot.
    /// Daily-or-more-often sources are refreshed every day; a longer
    /// interval keeps its own rule and joins only on the day it falls due.
    /// </summary>
    public static bool IsInDailyScope(int crawlFrequencyMinutes, DateTime? lastCheckedAtUtc, JobImportSlot slot)
        => crawlFrequencyMinutes <= MinutesPerDay
           || lastCheckedAtUtc is null
           || lastCheckedAtUtc.Value.AddMinutes(crawlFrequencyMinutes) <= slot.StartUtc + DueLookahead;

    /// <summary>"2026-10-26 05:00 (UTC+02:00)" — for notes and the admin status card.</summary>
    public static string DescribeLocal(DateTime utc, JobImportScheduleSettings settings)
    {
        var zone = ResolveTimeZone(settings.TimeZone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        var offset = zone.GetUtcOffset(local);
        return $"{local:yyyy-MM-dd HH:mm} (UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm})";
    }

    private static readonly Regex SecretPattern = new(@"(?i)\b(password|pwd|secret|token|access_token|api[_-]?key|apikey|key|sig|signature)\s*=\s*([^&;\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Masks credential-looking name=value pairs (connection-string
    /// passwords, query-string keys/tokens) before an exception message is
    /// logged or stored on a run row the admin portal displays.
    /// </summary>
    public static string? RedactSecrets(string? text)
        => string.IsNullOrEmpty(text) ? text : SecretPattern.Replace(text, "$1=[REDACTED]");

    private static DateTime ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}

public enum JobImportFailureKind
{
    // Worth retrying later today: timeouts, unreachable hosts, 5xx, 429,
    // and crawls interrupted by a restart or a time budget.
    Transient,
    // Retrying today will not help: invalid URL, 401/403/404/410, a page
    // that fetched fine but could not be parsed. Tomorrow's run tries again.
    Permanent
}

/// <summary>
/// Classifies a FAILED per-source run from what JobImportRuns already
/// stores (HTTP status + the failure message our own fetcher/importer
/// wrote), so the decision survives a restart with no extra column.
/// </summary>
public static class JobImportFailureClassifier
{
    public static JobImportFailureKind Classify(int? httpStatusCode, string? failureMessage)
    {
        var message = failureMessage ?? string.Empty;

        // Interrupted by our own time budget, a shutdown or the stuck-run
        // reaper — the source never had a fair chance.
        if (message.Contains("Stopped after", StringComparison.OrdinalIgnoreCase)
            || message.Contains("did not report back", StringComparison.OrdinalIgnoreCase)
            || message.Contains("interrupted", StringComparison.OrdinalIgnoreCase))
        {
            return JobImportFailureKind.Transient;
        }

        if (httpStatusCode is null)
        {
            // The fetcher refused to even try: the configured URL is
            // empty or not http(s). That is configuration, not weather.
            return message.StartsWith("Source URL", StringComparison.OrdinalIgnoreCase)
                ? JobImportFailureKind.Permanent
                : JobImportFailureKind.Transient;
        }

        var status = httpStatusCode.Value;
        if (status is 408 or 425 or 429 || status >= 500) return JobImportFailureKind.Transient;
        if (status >= 400) return JobImportFailureKind.Permanent;

        // Fetched successfully but failed afterwards. Parsing/size problems
        // repeat identically on a retry; an unexpected error may not.
        return message.StartsWith("Unexpected error", StringComparison.OrdinalIgnoreCase)
            ? JobImportFailureKind.Transient
            : JobImportFailureKind.Permanent;
    }
}

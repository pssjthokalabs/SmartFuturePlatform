using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Jobs;
using SmartFuture.Shared.Enums.Communication;
using SmartFuture.Shared.Enums.Jobs;
using SmartFuture.Shared.Enums.Notifications;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Jobs;

public class JobAlertService : IJobAlertService
{
    private const int MaxJobsPerDigest = 12;
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private readonly IAppDbContext _dbContext;
    private readonly IJobSettingsService _settingsService;
    private readonly INotificationService _notifications;
    private readonly ILogger<JobAlertService> _logger;

    public JobAlertService(IAppDbContext dbContext, IJobSettingsService settingsService, INotificationService notifications, ILogger<JobAlertService> logger)
    {
        _dbContext = dbContext;
        _settingsService = settingsService;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<Result<JobAlertRunSummaryDto>> RunDigestAsync(JobAlertFrequency frequency, bool dryRun, CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await _settingsService.GetOrCreateAsync(cancellationToken);
            var now = DateTime.UtcNow;
            var windowStart = ResolveWindowStart(frequency, now);

            var summary = new JobAlertRunSummaryDto
            {
                AlertsEnabled = settings.JobAlertsEnabled,
                WindowStartUtc = windowStart,
                WindowEndUtc = now
            };

            if (!settings.JobAlertsEnabled)
            {
                summary.Message = "Job alerts are disabled in Job Settings. Nothing was sent.";
                return Result<JobAlertRunSummaryDto>.Success(summary, summary.Message);
            }

            // Candidate jobs for the window, loaded ONCE and matched in
            // memory per subscriber. The window is small (a day or a
            // week of new listings), so this is far cheaper than a query
            // per subscriber.
            var candidates = await _dbContext.JobOpportunities.AsNoTracking()
                .Where(j => j.Status == JobOpportunityStatus.Active
                    && (j.ClosingDateUtc == null || j.ClosingDateUtc >= now)
                    && j.CreatedAtUtc >= windowStart)
                .OrderByDescending(j => j.IsFeatured).ThenByDescending(j => j.CreatedAtUtc)
                .Take(500)
                .ToListAsync(cancellationToken);

            var preferences = await _dbContext.JobAlertPreferences
                .Where(p => p.IsSubscribed && p.Frequency == frequency)
                .ToListAsync(cancellationToken);

            summary.SubscribersConsidered = preferences.Count;

            if (candidates.Count == 0)
            {
                summary.Skipped = preferences.Count;
                summary.Message = "No new jobs were published in this window. Nothing was sent.";
                return Result<JobAlertRunSummaryDto>.Success(summary, summary.Message);
            }

            foreach (var preference in preferences)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var user = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == preference.UserId, cancellationToken);
                if (user is null || !user.IsActive || string.IsNullOrWhiteSpace(user.Email))
                {
                    summary.Skipped++;
                    LogDelivery(preference, JobAlertDeliveryStatus.Skipped, windowStart, now, 0, user?.Email, null, "Subscriber has no active account or email address.");
                    continue;
                }

                // Controlled QA accounts never receive real marketing mail.
                if (user.IsTestAccount)
                {
                    summary.Skipped++;
                    LogDelivery(preference, JobAlertDeliveryStatus.Skipped, windowStart, now, 0, user.Email, null, "Test account — alerts suppressed.");
                    continue;
                }

                var matches = MatchJobs(candidates, preference).Take(MaxJobsPerDigest).ToList();
                if (matches.Count == 0)
                {
                    summary.Skipped++;
                    LogDelivery(preference, JobAlertDeliveryStatus.Skipped, windowStart, now, 0, user.Email, null, "No jobs matched this subscriber's preferences.");
                    continue;
                }

                var subject = matches.Count == 1
                    ? $"New job opportunity: {matches[0].Title}"
                    : $"{matches.Count} new job opportunities on Smart Future";

                if (dryRun)
                {
                    summary.EmailsQueued++;
                    LogDelivery(preference, JobAlertDeliveryStatus.Skipped, windowStart, now, matches.Count, user.Email, subject, "Dry run — nothing was sent.");
                    continue;
                }

                var body = BuildDigestBody(user.FirstName, matches, preference.UnsubscribeToken);
                var send = await _notifications.SendAsync(new SendNotificationRequestDto
                {
                    UserId = user.Id,
                    Channel = NotificationChannel.Email,
                    Type = NotificationType.JobAlertDigest,
                    RecipientEmail = user.Email,
                    Subject = subject,
                    Body = body.PlainText,
                    IsHtml = true,
                    HtmlBody = body.Html,
                    SenderType = EmailSenderType.NoReply,
                    RelatedEntityType = "JobAlert",
                    RelatedEntityId = user.Id
                });

                if (send.IsSuccess)
                {
                    summary.EmailsQueued++;
                    preference.LastSentAtUtc = now;
                    LogDelivery(preference, JobAlertDeliveryStatus.Sent, windowStart, now, matches.Count, user.Email, subject, null, sentAt: now);
                }
                else
                {
                    summary.Failed++;
                    LogDelivery(preference, JobAlertDeliveryStatus.Failed, windowStart, now, matches.Count, user.Email, subject, send.Message);
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            summary.Message = dryRun
                ? $"Dry run: {summary.EmailsQueued} subscriber(s) would receive a digest, {summary.Skipped} skipped."
                : $"{summary.EmailsQueued} digest(s) queued, {summary.Skipped} skipped, {summary.Failed} failed.";

            return Result<JobAlertRunSummaryDto>.Success(summary, summary.Message);
        }
        catch (OperationCanceledException)
        {
            return Result<JobAlertRunSummaryDto>.Failure(ErrorCodes.EXCEPTION, "The alert run was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error running job alert digest");
            return Result<JobAlertRunSummaryDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while running job alerts.");
        }
    }

    public async Task<Result<IReadOnlyList<JobOpportunityDto>>> PreviewMatchesAsync(Guid userId, JobAlertFrequency frequency, CancellationToken cancellationToken = default)
    {
        try
        {
            if (userId == Guid.Empty)
                return Result<IReadOnlyList<JobOpportunityDto>>.Failure(ErrorCodes.BAD_REQUEST, "User id is required.");

            var preference = await _dbContext.JobAlertPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
            if (preference is null)
                return Result<IReadOnlyList<JobOpportunityDto>>.Failure(ErrorCodes.NOT_FOUND, "This user has no job alert preference on file.");

            var now = DateTime.UtcNow;
            var windowStart = ResolveWindowStart(frequency, now);

            var candidates = await _dbContext.JobOpportunities.AsNoTracking()
                .Where(j => j.Status == JobOpportunityStatus.Active
                    && (j.ClosingDateUtc == null || j.ClosingDateUtc >= now)
                    && j.CreatedAtUtc >= windowStart)
                .OrderByDescending(j => j.IsFeatured).ThenByDescending(j => j.CreatedAtUtc)
                .Take(500)
                .ToListAsync(cancellationToken);

            var matches = MatchJobs(candidates, preference).Take(MaxJobsPerDigest)
                .Select(MapToCardDto)
                .ToList();

            return Result<IReadOnlyList<JobOpportunityDto>>.Success(matches);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error previewing job alert matches for {UserId}", userId);
            return Result<IReadOnlyList<JobOpportunityDto>>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while previewing matches.");
        }
    }

    public async Task<Result<PagedResult<JobAlertDeliveryLogDto>>> SearchDeliveryLogsAsync(Guid? userId, int? page, int? pageSize, CancellationToken cancellationToken = default)
    {
        try
        {
            var resolvedPage = Math.Max(1, page ?? 1);
            var resolvedPageSize = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);

            var query = _dbContext.JobAlertDeliveryLogs.AsNoTracking().AsQueryable();
            if (userId.HasValue && userId.Value != Guid.Empty) query = query.Where(l => l.UserId == userId.Value);

            var totalCount = await query.CountAsync(cancellationToken);
            var rows = await query
                .OrderByDescending(l => l.CreatedAtUtc)
                .Skip((resolvedPage - 1) * resolvedPageSize).Take(resolvedPageSize)
                .ToListAsync(cancellationToken);

            var items = rows.Select(l => new JobAlertDeliveryLogDto
            {
                Id = l.Id,
                UserId = l.UserId,
                RecipientEmail = l.RecipientEmail,
                Status = l.Status,
                StatusLabel = l.Status.ToString(),
                Frequency = l.Frequency,
                WindowStartUtc = l.WindowStartUtc,
                WindowEndUtc = l.WindowEndUtc,
                JobCount = l.JobCount,
                Subject = l.Subject,
                FailureMessage = l.FailureMessage,
                SentAtUtc = l.SentAtUtc,
                CreatedAtUtc = l.CreatedAtUtc
            }).ToList();

            return Result<PagedResult<JobAlertDeliveryLogDto>>.Success(new PagedResult<JobAlertDeliveryLogDto>(items, resolvedPage, resolvedPageSize, totalCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching job alert delivery logs");
            return Result<PagedResult<JobAlertDeliveryLogDto>>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while loading alert logs.");
        }
    }

    // ─── Matching ─────────────────────────────────────────────────────
    //
    // Empty preference lists mean "everything" — a subscriber who never
    // set filters still gets the digest. Within a facet, ANY match
    // qualifies; across facets, ALL configured facets must match.

    private static IEnumerable<JobOpportunity> MatchJobs(IEnumerable<JobOpportunity> candidates, JobAlertPreference preference)
    {
        var categories = JobTextUtilities.ReadStringList(preference.CategoriesJson);
        var locations = JobTextUtilities.ReadStringList(preference.LocationsJson);
        var keywords = JobTextUtilities.ReadStringList(preference.KeywordsJson);

        foreach (var job in candidates)
        {
            if (categories.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(job.Category)) continue;
                if (!categories.Any(c => job.Category.Contains(c, StringComparison.OrdinalIgnoreCase))) continue;
            }

            if (locations.Count > 0)
            {
                var haystack = $"{job.Location} {job.City} {job.Province}";
                if (!locations.Any(l => haystack.Contains(l, StringComparison.OrdinalIgnoreCase))) continue;
            }

            if (keywords.Count > 0)
            {
                var haystack = $"{job.Title} {job.Summary} {job.Category} {job.CompanyName}";
                if (!keywords.Any(k => haystack.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
            }

            yield return job;
        }
    }

    private static DateTime ResolveWindowStart(JobAlertFrequency frequency, DateTime nowUtc) => frequency switch
    {
        JobAlertFrequency.Weekly => nowUtc.AddDays(-7),
        // Immediate has no real-time sender yet, so it behaves like Daily
        // rather than silently sending nothing.
        _ => nowUtc.AddDays(-1)
    };

    private void LogDelivery(JobAlertPreference preference, JobAlertDeliveryStatus status, DateTime windowStart, DateTime windowEnd, int jobCount,
        string? email, string? subject, string? failureMessage, DateTime? sentAt = null)
    {
        _dbContext.JobAlertDeliveryLogs.Add(new JobAlertDeliveryLog
        {
            UserId = preference.UserId,
            Status = status,
            Frequency = preference.Frequency,
            WindowStartUtc = windowStart,
            WindowEndUtc = windowEnd,
            JobCount = jobCount,
            RecipientEmail = email,
            Subject = subject,
            FailureMessage = failureMessage,
            SentAtUtc = sentAt
        });
    }

    // ─── Email body ───────────────────────────────────────────────────

    private (string Html, string PlainText) BuildDigestBody(string firstName, IReadOnlyList<JobOpportunity> jobs, string? unsubscribeToken)
    {
        var greeting = string.IsNullOrWhiteSpace(firstName) ? "Hi there" : $"Hi {firstName}";

        var html = new StringBuilder();
        html.Append("<p>").Append(WebEncode(greeting)).Append(",</p>");
        html.Append("<p>Here are the latest job opportunities on Smart Future:</p><ul>");

        var text = new StringBuilder();
        text.AppendLine($"{greeting},").AppendLine().AppendLine("Here are the latest job opportunities on Smart Future:").AppendLine();

        foreach (var job in jobs)
        {
            var where = string.IsNullOrWhiteSpace(job.Location) ? "Location not stated" : job.Location!;
            var company = string.IsNullOrWhiteSpace(job.CompanyName) ? job.SourceName : job.CompanyName!;
            var closing = job.ClosingDateUtc is null ? string.Empty : $" · Closes {job.ClosingDateUtc:d MMM yyyy}";

            html.Append("<li><strong>").Append(WebEncode(job.Title)).Append("</strong><br/>")
                .Append(WebEncode($"{company} · {where}{closing}")).Append("</li>");

            text.AppendLine($"- {job.Title}").AppendLine($"  {company} · {where}{closing}");
        }

        html.Append("</ul>");
        text.AppendLine();

        if (!string.IsNullOrWhiteSpace(unsubscribeToken))
        {
            // Relative path — the notification layer/email template owns
            // the absolute host, so this never hard-codes an environment.
            var link = $"/jobs/unsubscribe?token={unsubscribeToken}";
            html.Append("<p style=\"font-size:12px;color:#666\">Don't want these emails? <a href=\"").Append(link).Append("\">Unsubscribe</a>.</p>");
            text.AppendLine($"Unsubscribe: {link}");
        }

        return (html.ToString(), text.ToString());
    }

    private static string WebEncode(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private static JobOpportunityDto MapToCardDto(JobOpportunity j) => new()
    {
        Id = j.Id,
        Slug = j.Slug,
        Title = j.Title,
        CompanyName = j.CompanyName,
        Location = j.Location,
        City = j.City,
        Province = j.Province,
        Category = j.Category,
        WorkplaceType = j.WorkplaceType,
        WorkplaceTypeLabel = j.WorkplaceType.ToString(),
        SalaryText = j.SalaryText,
        Summary = j.Summary,
        PostedDateUtc = j.PostedDateUtc,
        ClosingDateUtc = j.ClosingDateUtc,
        IsFeatured = j.IsFeatured,
        SourceName = j.SourceName,
        SourceUrl = j.SourceUrl
    };
}

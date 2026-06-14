using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Shared.Enums.Communication;
using SmartFuture.Shared.Enums.Notifications;

namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Phase 0F-notify implementation. Deduped, flag-gated, best-effort billing
/// notifications. Reuses <see cref="INotificationService"/> (which persists
/// an OutboundNotification + dispatches) and uses those rows as the dedupe
/// store via (Type, RelatedEntityType, RelatedEntityId).
/// </summary>
public sealed class BillingNotificationService : IBillingNotificationService
{
    private const string InvoiceEntity = "Invoice";

    private readonly IAppDbContext _dbContext;
    private readonly INotificationService _notifications;
    private readonly AutoBillingSettings _settings;
    private readonly ILogger<BillingNotificationService> _logger;

    public BillingNotificationService(
        IAppDbContext dbContext,
        INotificationService notifications,
        IOptions<AutoBillingSettings> settings,
        ILogger<BillingNotificationService> logger)
    {
        _dbContext = dbContext;
        _notifications = notifications;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task NotifyInvoiceGeneratedAsync(
        Guid invoiceId, string invoiceNumber, Guid userId,
        decimal amount, DateTime? dueAtUtc, string currencyCode,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.SendInvoiceGeneratedEmails)
            return;

        var due = dueAtUtc.HasValue ? dueAtUtc.Value.ToString("yyyy-MM-dd") : "soon";
        var subject = $"Your SmartFuture invoice {invoiceNumber}";
        var body = $@"Hi there,

Your recurring service invoice {invoiceNumber} for {amount:0.00} {currencyCode} has been issued and is due on {due}.

You can pay it from your SmartFuture account at any time. If you have automatic billing enabled, we'll attempt the payment on the due date.

— SmartFuture";

        await TrySendCustomerAsync(
            NotificationType.RecurringInvoiceGenerated, invoiceId, userId,
            subject, body, "invoice_generated", cancellationToken);
    }

    public async Task NotifyGraceCandidateAsync(
        Guid invoiceId, string invoiceNumber, Guid userId,
        decimal balanceDue, double daysOverdue,
        CancellationToken cancellationToken = default)
    {
        var overdue = Math.Max(0, (int)Math.Round(daysOverdue));

        // ── Customer warning (no "suspended" wording) ──
        if (_settings.SendGraceWarningEmails)
        {
            var subject = $"Action needed on SmartFuture invoice {invoiceNumber}";
            var body = $@"Hi there,

Invoice {invoiceNumber} for {balanceDue:0.00} is still unpaid ({overdue} day(s) past due).

Please settle it from your SmartFuture account to keep your services up to date. If you've already paid, thank you — you can ignore this message.

If you need help, reply to this email or contact support.

— SmartFuture";

            await TrySendCustomerAsync(
                NotificationType.BillingGraceCandidate, invoiceId, userId,
                subject, body, "grace_candidate", cancellationToken);
        }

        // ── Internal/ops alert ──
        if (_settings.SendInternalBillingAlerts
            && !string.IsNullOrWhiteSpace(_settings.InternalBillingAlertEmail))
        {
            var subject = $"[Billing] Suspension candidate — invoice {invoiceNumber}";
            var body = $@"Recurring billing suspension candidate (report-only).

Invoice: {invoiceNumber} ({invoiceId})
Customer: {userId}
Balance due: {balanceDue:0.00}
Days overdue: {overdue}

Retry chain exhausted and grace period expired. No automatic suspension is performed.";

            await TrySendInternalAsync(
                NotificationType.BillingInternalAlert, invoiceId,
                subject, body, "grace_candidate_internal", cancellationToken);
        }
    }

    // ─── helpers ───────────────────────────────────────────────────

    private async Task TrySendCustomerAsync(
        NotificationType type, Guid invoiceId, Guid userId,
        string subject, string body, string tag, CancellationToken cancellationToken)
    {
        try
        {
            if (await AlreadySentAsync(type, invoiceId, cancellationToken))
            {
                _logger.LogInformation("[billing][notify][deduped] tag={Tag} invoiceId={InvoiceId}", tag, invoiceId);
                return;
            }

            var email = await ResolveEmailAsync(userId, cancellationToken);
            if (string.IsNullOrWhiteSpace(email))
            {
                _logger.LogInformation("[billing][notify][skipped] tag={Tag} invoiceId={InvoiceId} reason=no_recipient", tag, invoiceId);
                return;
            }

            var result = await _notifications.SendAsync(new SendNotificationRequestDto
            {
                UserId = userId,
                Channel = NotificationChannel.Email,
                Type = type,
                RecipientEmail = email,
                Subject = subject,
                Body = body,
                SenderType = EmailSenderType.Payments,
                RelatedEntityType = InvoiceEntity,
                RelatedEntityId = invoiceId
            }, cancellationToken);

            _logger.LogInformation(
                "[billing][notify][sent] tag={Tag} invoiceId={InvoiceId} ok={Ok}",
                tag, invoiceId, result.IsSuccess);
        }
        catch (Exception ex)
        {
            // Best-effort — never block billing on a notification failure.
            _logger.LogWarning(ex, "[billing][notify][skipped] tag={Tag} invoiceId={InvoiceId} reason=exception", tag, invoiceId);
        }
    }

    private async Task TrySendInternalAsync(
        NotificationType type, Guid invoiceId,
        string subject, string body, string tag, CancellationToken cancellationToken)
    {
        try
        {
            if (await AlreadySentAsync(type, invoiceId, cancellationToken))
            {
                _logger.LogInformation("[billing][notify][deduped] tag={Tag} invoiceId={InvoiceId}", tag, invoiceId);
                return;
            }

            var result = await _notifications.SendAsync(new SendNotificationRequestDto
            {
                UserId = null,
                Channel = NotificationChannel.Email,
                Type = type,
                RecipientEmail = _settings.InternalBillingAlertEmail,
                Subject = subject,
                Body = body,
                SenderType = EmailSenderType.Support,
                RelatedEntityType = InvoiceEntity,
                RelatedEntityId = invoiceId
            }, cancellationToken);

            _logger.LogInformation(
                "[billing][notify][sent] tag={Tag} invoiceId={InvoiceId} ok={Ok}",
                tag, invoiceId, result.IsSuccess);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[billing][notify][skipped] tag={Tag} invoiceId={InvoiceId} reason=exception", tag, invoiceId);
        }
    }

    private Task<bool> AlreadySentAsync(NotificationType type, Guid invoiceId, CancellationToken cancellationToken)
        => _dbContext.OutboundNotifications
            .AsNoTracking()
            .AnyAsync(n => n.Type == type
                        && n.RelatedEntityType == InvoiceEntity
                        && n.RelatedEntityId == invoiceId,
                      cancellationToken);

    private async Task<string?> ResolveEmailAsync(Guid userId, CancellationToken cancellationToken)
        => await _dbContext.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(cancellationToken);
}

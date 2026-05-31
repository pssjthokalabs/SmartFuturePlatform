using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Communication;
using SmartFuture.Shared.Enums.Notifications;

namespace SmartFuture.Application.Payments;

/// <summary>
/// Thin wrapper around <see cref="INotificationService"/> that issues
/// the auto-billing notification family (success, failure, retry-failed,
/// final-failed). Every send is wrapped in try/catch so an email
/// outage NEVER blocks the billing pipeline — the failure is logged
/// and the caller continues.
///
/// The "send vs skip" decision is governed by
/// <see cref="AutoBillingSettings.SendFailureEmails"/>; even when
/// failure emails are off, the success email still fires (operators
/// have repeatedly asked for a positive receipt).
///
/// If the configured <c>INotificationService</c> backend isn't usable
/// (test-mode SMTP misconfigured, no SMTP creds, etc.) the call
/// returns a failure Result — we log <c>[AutoBillingEmail] skipped/not
/// configured</c> and move on. Returning bool keeps the caller
/// honest about whether to count this as a side effect.
/// </summary>
public class AutoBillingEmailService
{
    private readonly INotificationService _notifications;
    private readonly AutoBillingSettings _settings;
    private readonly ILogger<AutoBillingEmailService> _logger;

    public AutoBillingEmailService(
        INotificationService notifications,
        IOptions<AutoBillingSettings> settings,
        ILogger<AutoBillingEmailService> logger)
    {
        _notifications = notifications;
        _settings = settings.Value;
        _logger = logger;
    }

    public Task SendFailureEmailAsync(Invoice invoice, string recipientEmail, Guid? userId,
        decimal amount, string failureReason, int attemptNumber, int maxAttempts, DateTime? nextRetryUtc,
        CancellationToken cancellationToken)
    {
        if (!_settings.SendFailureEmails)
        {
            _logger.LogInformation(
                "[AutoBillingEmail] skipped — AutoBilling__SendFailureEmails=false (invoice {InvoiceNumber} attempt {AttemptNumber}/{MaxAttempts})",
                invoice.InvoiceNumber, attemptNumber, maxAttempts);
            return Task.CompletedTask;
        }

        var retriesRemaining = Math.Max(0, maxAttempts - attemptNumber);
        var isFinal = retriesRemaining == 0;

        string subject;
        string body;
        if (isFinal)
        {
            subject = "SmartFuture payment couldn't be processed — manual payment required";
            body = $@"Hi there,

We tried {maxAttempts} times to charge invoice {invoice.InvoiceNumber} for R{amount:0.00} but the payment didn't go through.

We won't try again automatically. Please open the invoice in SmartFuture and pay manually whenever you're ready.

Reason from the gateway: {failureReason}

If you need help, reply to this email or contact support.

— SmartFuture";
        }
        else if (attemptNumber == 1)
        {
            subject = "SmartFuture payment could not be processed";
            body = $@"Hi there,

We tried to charge invoice {invoice.InvoiceNumber} for R{amount:0.00} but the payment didn't go through.

We'll retry tomorrow and continue for up to {maxAttempts} attempts. You can also pay manually any time from the invoice page in SmartFuture.

Reason from the gateway: {failureReason}

— SmartFuture";
        }
        else
        {
            subject = $"SmartFuture payment retry {attemptNumber} of {maxAttempts} failed";
            body = $@"Hi there,

Retry {attemptNumber} of {maxAttempts} for invoice {invoice.InvoiceNumber} (R{amount:0.00}) didn't go through.

We'll retry again {(nextRetryUtc.HasValue ? nextRetryUtc.Value.ToString("yyyy-MM-dd") : "tomorrow")}. You can also pay manually from the invoice page in SmartFuture.

Reason from the gateway: {failureReason}

— SmartFuture";
        }

        return TrySendAsync(userId, recipientEmail, subject, body, invoice.Id, cancellationToken);
    }

    public Task SendSuccessEmailAsync(Invoice invoice, string recipientEmail, Guid? userId,
        decimal amount, DateTime nextBillingDateUtc,
        CancellationToken cancellationToken)
    {
        var subject = "SmartFuture payment received";
        var body = $@"Hi there,

We've received your payment of R{amount:0.00} against invoice {invoice.InvoiceNumber}. The invoice is now paid in full.

Your next billing date is {nextBillingDateUtc:yyyy-MM-dd}.

— SmartFuture";
        return TrySendAsync(userId, recipientEmail, subject, body, invoice.Id, cancellationToken);
    }

    private async Task TrySendAsync(Guid? userId, string recipientEmail, string subject, string body,
        Guid invoiceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recipientEmail))
        {
            _logger.LogInformation("[AutoBillingEmail] skipped — no recipient email for invoice {InvoiceId}", invoiceId);
            return;
        }

        try
        {
            var result = await _notifications.SendAsync(new SendNotificationRequestDto
            {
                UserId = userId,
                Channel = NotificationChannel.Email,
                Type = NotificationType.PaymentStatusChanged,
                RecipientEmail = recipientEmail,
                Subject = subject,
                Body = body,
                SenderType = EmailSenderType.Payments,
                RelatedEntityType = "Invoice",
                RelatedEntityId = invoiceId
            }, cancellationToken);

            if (!result.IsSuccess)
            {
                // Per AutoBilling__EmailFailuresDoNotBlockBilling — log
                // and continue. Do NOT throw. Operators can grep
                // [AutoBillingEmail] for the misses.
                _logger.LogWarning(
                    "[AutoBillingEmail] skipped/not configured — invoice {InvoiceId} send returned {Code} '{Message}'",
                    invoiceId, result.Code, result.Message);
            }
        }
        catch (Exception ex)
        {
            // Same policy — never block billing on email infrastructure
            // hiccups. If EmailFailuresDoNotBlockBilling were false we'd
            // rethrow; the default is true to favour completing the
            // payment lifecycle.
            if (!_settings.EmailFailuresDoNotBlockBilling)
            {
                _logger.LogError(ex,
                    "[AutoBillingEmail] hard-fail — invoice {InvoiceId} (EmailFailuresDoNotBlockBilling=false)",
                    invoiceId);
                throw;
            }
            _logger.LogWarning(ex,
                "[AutoBillingEmail] swallowed — invoice {InvoiceId} (EmailFailuresDoNotBlockBilling=true)",
                invoiceId);
        }
    }
}

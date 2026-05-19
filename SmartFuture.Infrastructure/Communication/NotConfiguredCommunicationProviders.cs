using Microsoft.Extensions.Logging;
using SmartFuture.Application.Communication.Sms;
using SmartFuture.Application.Communication.Verification;
using SmartFuture.Application.Communication.WhatsApp;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Infrastructure.Communication;

// Default registrations used when no real provider (Twilio, etc.) is
// configured. Returning a Result failure with PROVIDER_NOT_CONFIGURED
// keeps callers honest — code that requires SMS/WhatsApp/Verify can
// surface a meaningful error to the user (or rate-limit retries),
// rather than throwing or silently dropping messages.
//
// Real Twilio implementations are deferred to a follow-up phase.

public sealed class NotConfiguredSmsProvider : ISmsProvider
{
    private readonly ILogger<NotConfiguredSmsProvider> _logger;

    public NotConfiguredSmsProvider(ILogger<NotConfiguredSmsProvider> logger)
    {
        _logger = logger;
    }

    public Task<Result<SmsSendOutcome>> SendAsync(SmsSendRequest request, CancellationToken cancellationToken = default)
    {
        // Log the request shape (no body, no PII beyond destination)
        // so operators see that something tried to send.
        _logger.LogWarning(
            "[Sms] Send skipped — no SMS provider configured. To={To} CorrelationId={Correlation}",
            MaskPhone(request.ToPhoneNumber), request.CorrelationId);

        return Task.FromResult(Result<SmsSendOutcome>.Failure(
            ErrorCodes.SMS_NOT_CONFIGURED,
            "SMS delivery is not configured in this environment."));
    }

    private static string MaskPhone(string phone)
    {
        if (string.IsNullOrEmpty(phone) || phone.Length <= 4) return phone;
        return new string('•', phone.Length - 4) + phone[^4..];
    }
}

public sealed class NotConfiguredWhatsAppProvider : IWhatsAppProvider
{
    private readonly ILogger<NotConfiguredWhatsAppProvider> _logger;

    public NotConfiguredWhatsAppProvider(ILogger<NotConfiguredWhatsAppProvider> logger)
    {
        _logger = logger;
    }

    public Task<Result<WhatsAppSendOutcome>> SendAsync(WhatsAppSendRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "[WhatsApp] Send skipped — no WhatsApp provider configured. To={To} CorrelationId={Correlation}",
            MaskPhone(request.ToPhoneNumber), request.CorrelationId);

        return Task.FromResult(Result<WhatsAppSendOutcome>.Failure(
            ErrorCodes.PROVIDER_NOT_CONFIGURED,
            "WhatsApp delivery is not configured in this environment."));
    }

    private static string MaskPhone(string phone)
    {
        if (string.IsNullOrEmpty(phone) || phone.Length <= 4) return phone;
        return new string('•', phone.Length - 4) + phone[^4..];
    }
}

public sealed class NotConfiguredPhoneVerificationService : IPhoneVerificationService
{
    private readonly ILogger<NotConfiguredPhoneVerificationService> _logger;

    public NotConfiguredPhoneVerificationService(ILogger<NotConfiguredPhoneVerificationService> logger)
    {
        _logger = logger;
    }

    public Task<Result<PhoneVerificationStartOutcome>> StartAsync(PhoneVerificationStartRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "[PhoneVerification] Start skipped — no verification provider configured. To={To} Channel={Channel} Purpose={Purpose}",
            MaskPhone(request.ToPhoneNumber), request.Channel, request.Purpose);

        return Task.FromResult(Result<PhoneVerificationStartOutcome>.Failure(
            ErrorCodes.PROVIDER_NOT_CONFIGURED,
            "Phone verification is not configured in this environment."));
    }

    public Task<Result<PhoneVerificationCheckOutcome>> CheckAsync(PhoneVerificationCheckRequest request, CancellationToken cancellationToken = default)
    {
        // Never log the supplied code, even on failure.
        _logger.LogWarning(
            "[PhoneVerification] Check skipped — no verification provider configured. To={To} Purpose={Purpose}",
            MaskPhone(request.ToPhoneNumber), request.Purpose);

        return Task.FromResult(Result<PhoneVerificationCheckOutcome>.Failure(
            ErrorCodes.PROVIDER_NOT_CONFIGURED,
            "Phone verification is not configured in this environment."));
    }

    private static string MaskPhone(string phone)
    {
        if (string.IsNullOrEmpty(phone) || phone.Length <= 4) return phone;
        return new string('•', phone.Length - 4) + phone[^4..];
    }
}

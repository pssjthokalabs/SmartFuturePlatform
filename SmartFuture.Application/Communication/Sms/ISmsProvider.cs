using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Communication.Sms;

/// <summary>
/// Low-level send contract for SMS messages. Implementations either
/// hit a real provider (e.g. Twilio) or return a NOT_CONFIGURED
/// failure when no provider is wired up — never throws.
///
/// Concrete Twilio implementation is deferred to a follow-up phase;
/// for now <see cref="NotConfiguredSmsProvider"/> is registered so
/// callers can already depend on the abstraction.
/// </summary>
public interface ISmsProvider
{
    Task<Result<SmsSendOutcome>> SendAsync(SmsSendRequest request, CancellationToken cancellationToken = default);
}

public sealed record SmsSendRequest(string ToPhoneNumber, string Body, string? CorrelationId = null);

public sealed record SmsSendOutcome(string ProviderName, string ProviderMessageId);

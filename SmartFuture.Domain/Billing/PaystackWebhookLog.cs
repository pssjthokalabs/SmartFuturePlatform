using SmartFuture.Domain.Common;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Domain.Billing;

/// <summary>
/// Forensic audit row written by <c>PaystackNotifyHandler</c> on every
/// inbound webhook call, irrespective of acceptance. Created so an
/// operator can answer "did Paystack deliver?" / "why didn't the
/// invoice flip?" without needing to scrape ASP.NET logs.
///
/// What we DO store:
///   - body length, signature present/valid, event type, reference,
///     amount, currency, status (Paystack-reported)
///   - the resolved PaymentInitiation/Payment/Invoice ids when found
///   - whether the apply was attempted, whether it succeeded, the
///     apply error code/message
///   - the HTTP status we returned and a short outcome message
///   - environment name + UTC timestamps
///
/// What we deliberately DO NOT store:
///   - the merchant secret key
///   - the customer's authorization_code or full card number
///   - the full raw webhook body in production (would carry PII and
///     potentially an auth code). UAT/local may store a short safe
///     snippet if we ever need it; not stored today.
/// </summary>
public class PaystackWebhookLog : BaseEntity
{
    public PaymentProviderType Provider { get; set; } = PaymentProviderType.Paystack;

    public DateTime ReceivedAtUtc { get; set; }

    public int RawBodyLength { get; set; }
    public bool SignaturePresent { get; set; }
    public bool SignatureValid { get; set; }

    public string? Event { get; set; }
    public string? Reference { get; set; }
    public long? AmountSubunits { get; set; }
    public string? Currency { get; set; }
    public string? Status { get; set; }

    public Guid? PaymentInitiationId { get; set; }
    public Guid? PaymentId { get; set; }
    public Guid? InvoiceId { get; set; }

    public bool Accepted { get; set; }
    public string? OutcomeMessage { get; set; }
    public string? RejectionReason { get; set; }

    public bool ApplyAttempted { get; set; }
    public bool ApplySucceeded { get; set; }
    public string? ApplyErrorCode { get; set; }
    public string? ApplyErrorMessage { get; set; }

    public int HttpStatusReturned { get; set; } = 200;
    public string? EnvironmentName { get; set; }
}

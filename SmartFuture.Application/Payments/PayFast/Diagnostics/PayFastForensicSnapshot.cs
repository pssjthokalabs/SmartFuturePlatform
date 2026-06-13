namespace SmartFuture.Application.Payments.PayFast.Diagnostics;

/// <summary>
/// TEMPORARY UAT-ONLY forensic snapshot of a single PayFast ITN. One
/// instance is created per webhook hit when
/// <see cref="PaymentDiagnosticsOptions.CapturePayFastWebhook"/> is on.
/// Serialised to disk with camelCase JSON; never written when the
/// capture switch is off.
/// </summary>
public class PayFastForensicSnapshot
{
    /// <summary>Short id used in correlated log lines and the
    /// filename. 12 hex chars from <see cref="Guid.NewGuid"/>.</summary>
    public string DiagId { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;

    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? QueryString { get; set; }
    public string Scheme { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string? RemoteIp { get; set; }
    public string? UserAgent { get; set; }
    public string? ContentType { get; set; }
    public long? ContentLength { get; set; }
    public bool HasFormContentType { get; set; }

    /// <summary>All request headers with sensitive ones replaced by
    /// "[REDACTED]". See <c>WebhooksController</c> for the redaction
    /// list (Authorization, Cookie, Set-Cookie, plus any header name
    /// containing Secret / Key / Token / Password).</summary>
    public Dictionary<string, string> RedactedHeaders { get; set; } = new();

    /// <summary>The raw request body bytes decoded as UTF-8. Captured
    /// from <c>Request.Body</c> via <c>Request.EnableBuffering()</c>
    /// BEFORE form parsing so we can see what PayFast actually
    /// transmitted, even when the form parser later succeeded /
    /// failed. PII risk: includes customer name + email.</summary>
    public string? RawBody { get; set; }
    public int? RawBodyLength { get; set; }

    public int? FormKeyCount { get; set; }
    public List<string> FormKeys { get; set; } = new();
    /// <summary>The full set of parsed form key/value pairs. PII risk:
    /// includes customer name + email. Signature included verbatim
    /// (we need it to reproduce signature mismatches against the
    /// passphrase out-of-band).</summary>
    public Dictionary<string, string> FormValues { get; set; } = new();

    /// <summary>"preParsed" when ReadFormAsync supplied the dictionary,
    /// "rawBody" when the controller fell back to its own form parse
    /// (no form content type, or buffering disabled).</summary>
    public string? FieldsSource { get; set; }

    public string? GeneratedProviderEventId { get; set; }

    public PayFastParsedSummary ParsedSummary { get; set; } = new();

    public BridgeResultSnapshot? Result { get; set; }

    public string? ExceptionMessage { get; set; }
    public string? ExceptionStackTrace { get; set; }

    /// <summary>Filled in by the capture service after a successful
    /// file write so the controller can echo the path back in the
    /// final [forensic_file] log line.</summary>
    public string? WrittenFilePath { get; set; }
}

public class PayFastParsedSummary
{
    public string? MPaymentId { get; set; }
    public string? PfPaymentId { get; set; }
    public string? PaymentStatus { get; set; }
    public decimal? AmountGross { get; set; }
    public decimal? AmountFee { get; set; }
    public decimal? AmountNet { get; set; }
    public string? MerchantId { get; set; }
    /// <summary>True when the form had a non-empty `signature` field.
    /// We do NOT log the merchant key or passphrase anywhere; the
    /// signature itself is not a secret but we still mark presence
    /// rather than echo a no-op when missing.</summary>
    public bool SignaturePresent { get; set; }
}

public class BridgeResultSnapshot
{
    public bool Accepted { get; set; }
    public string? Message { get; set; }
    public Guid InboxId { get; set; }
    public string? ProviderEventId { get; set; }
}

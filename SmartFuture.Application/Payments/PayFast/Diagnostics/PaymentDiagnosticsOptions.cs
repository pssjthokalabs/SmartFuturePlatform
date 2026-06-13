namespace SmartFuture.Application.Payments.PayFast.Diagnostics;

/// <summary>
/// TEMPORARY UAT-ONLY forensic capture options for the PayFast ITN
/// pipeline. Keep this OFF in production — the captured JSON files
/// include the raw webhook body (PII: customer name + email) and the
/// request headers (redacted, but still sensitive). The whole feature
/// should be removed or hard-gated to non-Production once the live
/// PayFast settlement issue is understood.
///
/// Env vars (UAT):
///   PaymentDiagnostics__CapturePayFastWebhook=true
///   PaymentDiagnostics__WebhookLogPath=logs/webhooks
/// </summary>
public class PaymentDiagnosticsOptions
{
    public const string SectionName = "PaymentDiagnostics";

    /// <summary>When true, every PayFast webhook hit is dumped to a
    /// per-request JSON file under <see cref="WebhookLogPath"/>. Off
    /// by default. Production deployments MUST leave this off.</summary>
    public bool CapturePayFastWebhook { get; set; } = false;

    /// <summary>Where the forensic JSON files are written. Relative
    /// paths resolve against the API's content root. Defaults to
    /// "logs/webhooks". Directory is created on first write.</summary>
    public string WebhookLogPath { get; set; } = "logs/webhooks";
}

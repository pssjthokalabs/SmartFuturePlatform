namespace SmartFuture.Application.Openserve;

/// <summary>
/// Parses + logs a raw inbound Openserve payload (callback or event
/// notification) and feeds it into IOpenserveOrderUpdatePipeline. A
/// sibling to the payment WebhookInboxService — same shape of
/// responsibility (persist raw payload, never let a bad payload 500,
/// hand off to the shared update logic) — but intentionally NOT the
/// same class: Openserve payloads are a completely different wire
/// format from payment webhooks (spec §8), and forcing them through
/// PaymentWebhookRequestDto/IWebhookPayloadParser would mean bending a
/// payment-shaped contract to fit a non-payment integration.
/// </summary>
public interface IOpenserveInboundProcessor
{
    /// <summary>POST target for Openserve's ReplyToAddress — the async CREATE/UPDATE/CANCEL result callback (spec §1.7, §4.1.3.2 "Callback Response").</summary>
    Task<OpenserveInboundProcessResult> ProcessCallbackAsync(
        string rawPayload, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default);

    /// <summary>POST target for Openserve's registered event-notification endpoint — the four documented event types (spec §8).</summary>
    Task<OpenserveInboundProcessResult> ProcessEventAsync(
        string rawPayload, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default);
}

public class OpenserveInboundProcessResult
{
    public required bool Accepted { get; init; }
    public required string Message { get; init; }
}

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Webhooks;
using SmartFuture.Application.Webhooks.Dtos;
using SmartFuture.Shared.Enums.Webhooks;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Infrastructure.Webhooks;

/// <summary>
/// Best-effort JSON parser for inbound payment webhooks. Tries common provider field names.
/// Real provider-specific parsers should replace this DI registration when integration begins.
/// </summary>
public class BasicJsonWebhookPayloadParser : IWebhookPayloadParser
{
    private static readonly string[] EventTypeFields = { "eventType", "event_type", "type", "status", "event" };
    private static readonly string[] ProviderEventIdFields = { "id", "eventId", "event_id", "providerEventId" };
    private static readonly string[] PaymentReferenceFields = { "paymentReference", "payment_reference", "payment_id", "reference", "ref" };
    private static readonly string[] InvoiceReferenceFields = { "invoiceReference", "invoice_reference", "invoiceNumber", "invoice_number", "invoiceId", "invoice_id" };
    private static readonly string[] OrderReferenceFields = { "orderReference", "order_reference", "orderNumber", "order_number", "orderId", "order_id" };
    private static readonly string[] GatewayTransactionIdFields = { "transactionId", "transaction_id", "gatewayTransactionId", "txn_id", "txnId" };
    private static readonly string[] AmountFields = { "amount", "amount_paid", "total" };
    private static readonly string[] CurrencyFields = { "currency", "currencyCode", "currency_code" };
    private static readonly string[] CreatedAtFields = { "createdAt", "created_at", "timestamp", "occurred_at" };
    private static readonly string[] IdempotencyKeyFields = { "idempotencyKey", "idempotency_key", "x-idempotency-key" };

    private readonly ILogger<BasicJsonWebhookPayloadParser> _logger;

    public BasicJsonWebhookPayloadParser(ILogger<BasicJsonWebhookPayloadParser> logger)
    {
        _logger = logger;
    }

    public Task<Result<ParsedPaymentWebhookDto>> ParsePaymentWebhookAsync(PaymentWebhookRequestDto request, CancellationToken cancellationToken = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.RawPayload))
            return Task.FromResult(Result<ParsedPaymentWebhookDto>.Failure(
                ErrorCodes.BAD_REQUEST, "Raw payload is required."));

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(request.RawPayload);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Webhook payload from {Provider} is not valid JSON; cannot parse fields.",
                request.ProviderName);
            return Task.FromResult(Result<ParsedPaymentWebhookDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "Payload is not valid JSON."));
        }

        try
        {
            var root = doc.RootElement;

            var rawStatus = FindStringValue(root, EventTypeFields);
            var eventType = MapEventType(rawStatus);

            var parsed = new ParsedPaymentWebhookDto
            {
                EventType = eventType,
                ProviderEventId = FindStringValue(root, ProviderEventIdFields),
                IdempotencyKey = FindStringValue(root, IdempotencyKeyFields),
                PaymentReference = FindStringValue(root, PaymentReferenceFields),
                InvoiceReference = FindStringValue(root, InvoiceReferenceFields),
                OrderReference = FindStringValue(root, OrderReferenceFields),
                GatewayTransactionId = FindStringValue(root, GatewayTransactionIdFields),
                Amount = FindDecimalValue(root, AmountFields),
                CurrencyCode = NormaliseCurrency(FindStringValue(root, CurrencyFields)),
                ProviderCreatedAtUtc = FindDateTimeValue(root, CreatedAtFields)
            };

            parsed.ParsedSummaryJson = JsonSerializer.Serialize(new
            {
                eventType = parsed.EventType,
                providerEventId = parsed.ProviderEventId,
                paymentReference = parsed.PaymentReference,
                invoiceReference = parsed.InvoiceReference,
                orderReference = parsed.OrderReference,
                gatewayTransactionId = parsed.GatewayTransactionId,
                amount = parsed.Amount,
                currencyCode = parsed.CurrencyCode,
                providerCreatedAtUtc = parsed.ProviderCreatedAtUtc,
                rawStatus
            });

            return Task.FromResult(Result<ParsedPaymentWebhookDto>.Success(parsed));
        }
        finally
        {
            doc.Dispose();
        }
    }

    private static WebhookEventType MapEventType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return WebhookEventType.Unknown;

        var v = value.Trim().ToLowerInvariant();

        return v switch
        {
            "paid" or "success" or "succeeded" or "completed" or "payment.completed" or "payment_completed"
                => WebhookEventType.PaymentCompleted,

            "failed" or "error" or "declined" or "payment.failed" or "payment_failed"
                => WebhookEventType.PaymentFailed,

            "pending" or "payment.pending" or "payment_pending"
                => WebhookEventType.PaymentPending,

            "cancelled" or "canceled" or "payment.cancelled" or "payment_cancelled"
                => WebhookEventType.PaymentCancelled,

            "refunded" or "refund" or "payment.refunded" or "payment_refunded"
                => WebhookEventType.PaymentRefunded,

            "reversed" or "chargeback" or "payment.reversed" or "payment_reversed"
                => WebhookEventType.PaymentReversed,

            "mandate.created" or "mandate_created"
                => WebhookEventType.MandateCreated,

            "mandate.activated" or "mandate_activated"
                => WebhookEventType.MandateActivated,

            "mandate.cancelled" or "mandate.canceled" or "mandate_cancelled"
                => WebhookEventType.MandateCancelled,

            _ => WebhookEventType.Unknown
        };
    }

    private static string? FindStringValue(JsonElement root, string[] candidateNames)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in candidateNames)
        {
            if (TryGetCaseInsensitive(root, name, out var element))
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    var value = element.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                }
                else if (element.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                {
                    return element.ToString();
                }
            }
        }

        return null;
    }

    private static decimal? FindDecimalValue(JsonElement root, string[] candidateNames)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in candidateNames)
        {
            if (TryGetCaseInsensitive(root, name, out var element))
            {
                if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var num))
                    return num;

                if (element.ValueKind == JsonValueKind.String)
                {
                    var s = element.GetString();
                    if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                        return parsed;
                }
            }
        }

        return null;
    }

    private static DateTime? FindDateTimeValue(JsonElement root, string[] candidateNames)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in candidateNames)
        {
            if (TryGetCaseInsensitive(root, name, out var element))
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    var s = element.GetString();
                    if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                        return dt;
                }
                else if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var epoch))
                {
                    // Heuristic: treat as Unix seconds if ≤ 10 digits, else ms.
                    return epoch > 9_999_999_999L
                        ? DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime
                        : DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
                }
            }
        }

        return null;
    }

    private static bool TryGetCaseInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? NormaliseCurrency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().ToUpperInvariant();
        return v.Length > 3 ? v[..3] : v;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SmartFuture.Application.Openserve.Dtos;

// Inbound wire-format shapes. Deliberately loose: the spec itself warns
// "the GET API returns much more details than what is presented in
// this example" (§4.6.1) and the response envelope mixes casing
// (ErrorResult vs Result/Payload). All of these are deserialized with
// PropertyNameCaseInsensitive=true (see OpenserveApiClient) and the
// deep productOrder/cancelproductOrder catalogue tree is read via
// JsonElement + ExtractOrderFacts rather than a closed POCO graph, so
// an unrecognised or additional property never breaks parsing.

/// <summary>Sync acknowledgement — Postman UC 1 example: {"ErrorResult":{"code":"0","reason":"OS-ESB-ACK","message":"Order received for processing. Order Id = 1742148. State = Validated"}}. The same ErrorResult shape also comes back with HTTP 400 for business rejections (code "1", e.g. reason "E84119").</summary>
public class OpenserveAckResponse
{
    public OpenserveErrorResult? ErrorResult { get; set; }
}

public class OpenserveErrorResult
{
    [JsonConverter(typeof(OpenserveLenientStringConverter))]
    public string? Code { get; set; }

    public string? Reason { get; set; }
    public string? Message { get; set; }
}

/// <summary>Superset used only to read an error out of a non-2xx body: Openserve uses ErrorResult on ordering calls and Result on cancel/suspend-style calls.</summary>
public class OpenserveErrorEnvelope
{
    public OpenserveErrorResult? ErrorResult { get; set; }
    public OpenserveResult? Result { get; set; }
}

/// <summary>Async callback POSTed to ReplyToAddress, and the response body of Cancel Product Order — e.g. {"Result":{"ResultCode":"0",...},"Payload":{"order":{"id":"302050"},"state":"Acknowledged"}}</summary>
public class OpenserveResultEnvelope
{
    public OpenserveResult? Result { get; set; }
    public OpenserveCallbackPayload? Payload { get; set; }
}

public class OpenserveResult
{
    /// <summary>Openserve sends this both as a string ("0" — cancel/callback samples) and as a number (0 — suspend/GetServiceDetails samples in the Postman collection); both normalise to a string here.</summary>
    [JsonConverter(typeof(OpenserveLenientStringConverter))]
    public string? ResultCode { get; set; }

    public string? ResultMsgCode { get; set; }
    public string? ResultMsg { get; set; }
}

/// <summary>Reads a JSON string, number or boolean into a string so a type drift on Openserve's side never turns a valid response into a parse failure.</summary>
public sealed class OpenserveLenientStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonTokenType.True => "true",
        JsonTokenType.False => "false",
        JsonTokenType.Null => null,
        _ => SkipAndReturnNull(ref reader)
    };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }

    private static string? SkipAndReturnNull(ref Utf8JsonReader reader)
    {
        reader.Skip();
        return null;
    }
}

public class OpenserveCallbackPayload
{
    public OpenserveOrderRef? Order { get; set; }
    public string? State { get; set; }
    public string? Id { get; set; }
    public string? EffectiveCancellationDate { get; set; }
}

/// <summary>Product Order Notification event envelope (spec §8) — same shape for all four event types.</summary>
public class OpenserveEventEnvelope
{
    public long? EventId { get; set; }
    public string? EventTime { get; set; }
    public string? EventType { get; set; }
    public string? CorrelationId { get; set; }
    public string? Description { get; set; }
    public string? Domain { get; set; }
    public string? Priority { get; set; }

    /// <summary>Spec's own field name — literally "timeOcurred" (typo preserved verbatim in ITSD-179559 Rev 04.002), not "timeOccurred".</summary>
    public string? TimeOcurred { get; set; }

    public OpenserveEventBody? Event { get; set; }
}

public class OpenserveEventBody
{
    /// <summary>Present for ProductOrderCreateEvent / ProductOrderStateChangeEvent.</summary>
    public JsonElement? ProductOrder { get; set; }

    /// <summary>Present for CancelProductOrderCreateEvent / CancelProductOrderStateChangeEvent (spec's own lowercase-c key).</summary>
    public JsonElement? Cancelproductorder { get; set; }
}

/// <summary>Facts plucked defensively out of the deep productOrder/cancelproductOrder catalogue tree — only what SmartFuture actually needs, tolerant of everything else.</summary>
public sealed record OpenserveOrderFacts(string? Id, string? State, string? OrderName, string? ServiceOrderNumber, string? OrderDate, string? OrderType = null, string? CircuitNumber = null);

public static class OpenserveOrderFactsExtractor
{
    private static readonly Regex OrderIdFromAckMessage = new(
        @"Order\s*ID\s*=\s*(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StateFromAckMessage = new(
        @"State\s*=\s*(?<state>[A-Za-z ]+?)\.?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The sync ack's order id/state aren't structured fields — they're
    /// embedded in a free-text message like "Order Received for
    /// Processing. Order ID = 302114. State = Validated." Best-effort
    /// only: the real, structured confirmation is the async callback.
    /// </summary>
    public static (string? OrderId, string? State) TryParseAckMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return (null, null);
        var idMatch = OrderIdFromAckMessage.Match(message);
        var stateMatch = StateFromAckMessage.Match(message);
        return (
            idMatch.Success ? idMatch.Groups["id"].Value : null,
            stateMatch.Success ? stateMatch.Groups["state"].Value.Trim() : null);
    }

    /// <summary>Reads id/state/OrderName/ServiceOrderNumber/orderDate/@type/circuit number out of a productOrder or cancelproductOrder JsonElement without binding the whole catalogue tree.</summary>
    public static OpenserveOrderFacts Extract(JsonElement order)
    {
        string? id = GetString(order, "id");
        string? state = GetString(order, "state");
        string? orderDate = GetString(order, "orderDate");
        string? orderType = GetString(order, "@type");
        string? orderName = null;
        string? serviceOrderNumber = null;

        if (order.ValueKind == JsonValueKind.Object
            && order.TryGetProperty("orderCharacteristic", out var chars)
            && chars.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in chars.EnumerateArray())
            {
                var name = GetString(c, "name");
                if (string.Equals(name, "OrderName", StringComparison.OrdinalIgnoreCase))
                    orderName = GetString(c, "value");
                else if (string.Equals(name, "ServiceOrderNumber", StringComparison.OrdinalIgnoreCase))
                    serviceOrderNumber = GetString(c, "value");
            }
        }

        return new OpenserveOrderFacts(id, state, orderName, serviceOrderNumber, orderDate, orderType, FindCircuitNumber(order));
    }

    // Postman "Query Order Details" example: productOrderItem[].product.realizingService[].name
    // holds the B-number (also nested one level deeper under productRelationship in the PDF's
    // §4.6.1 sample). An explicit "Circuit Number" serviceCharacteristic wins when present.
    private static string? FindCircuitNumber(JsonElement order)
    {
        if (order.ValueKind != JsonValueKind.Object || !order.TryGetProperty("productOrderItem", out var items) || items.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.Object) continue;
            var found = FindCircuitInProduct(product, depth: 0);
            if (!string.IsNullOrWhiteSpace(found)) return found;
        }
        return null;
    }

    private static string? FindCircuitInProduct(JsonElement product, int depth)
    {
        if (depth > 3 || product.ValueKind != JsonValueKind.Object) return null;

        if (product.TryGetProperty("realizingService", out var services) && services.ValueKind == JsonValueKind.Array)
        {
            foreach (var service in services.EnumerateArray())
            {
                if (service.TryGetProperty("serviceCharacteristic", out var serviceChars) && serviceChars.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in serviceChars.EnumerateArray())
                    {
                        if (string.Equals(GetString(c, "name"), "Circuit Number", StringComparison.OrdinalIgnoreCase)
                            && GetString(c, "value") is { Length: > 0 } explicitCircuit)
                            return explicitCircuit;
                    }
                }

                // A realizingService "name" is only a circuit when it looks like one (B-number or
                // a numeric DN) — a fresh provide uses an internal "agreement_timestamp_..." name instead.
                var serviceName = GetString(service, "name");
                if (serviceName is { Length: > 0 } && LooksLikeCircuitNumber(serviceName)) return serviceName;
            }
        }

        if (product.TryGetProperty("productRelationship", out var relationships) && relationships.ValueKind == JsonValueKind.Array)
        {
            foreach (var rel in relationships.EnumerateArray())
            {
                if (rel.TryGetProperty("product", out var child))
                {
                    var found = FindCircuitInProduct(child, depth + 1);
                    if (!string.IsNullOrWhiteSpace(found)) return found;
                }
            }
        }

        return null;
    }

    private static bool LooksLikeCircuitNumber(string value)
        => Regex.IsMatch(value, @"^(B\d{5,}|0\d{9})$", RegexOptions.IgnoreCase);

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(propertyName, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.ToString(),
            _ => null
        };
    }
}

/// <summary>GET {BaseUrl}/{ws-ispcode}/productorder?...&searchKey=...&searchValue=... list response (PDF §4.6.2). Not present in the provisioned Postman collection — unused; kept only as a documented shape.</summary>
public class OpenserveOrderListResponse
{
    public int? TotalObjects { get; set; }
    public List<OpenserveOrderListItem>? Orders { get; set; }
}

public class OpenserveOrderListItem
{
    public string? Id { get; set; }
    public string? State { get; set; }
    public string? OrderDate { get; set; }
}

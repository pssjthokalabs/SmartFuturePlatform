using System.Text.Json;
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

/// <summary>Sync HTTP 200 acknowledgement — e.g. {"ErrorResult":{"code":"0","reason":"OS-ESB-ACK","message":"Order Received for Processing. Order ID = 302114. State = Validated."}}</summary>
public class OpenserveAckResponse
{
    public OpenserveErrorResult? ErrorResult { get; set; }
}

public class OpenserveErrorResult
{
    public string? Code { get; set; }
    public string? Reason { get; set; }
    public string? Message { get; set; }
}

/// <summary>Async callback POSTed to ReplyToAddress, and the response body of Cancel Product Order — e.g. {"Result":{"ResultCode":"0",...},"Payload":{"order":{"id":"302050"},"state":"Acknowledged"}}</summary>
public class OpenserveResultEnvelope
{
    public OpenserveResult? Result { get; set; }
    public OpenserveCallbackPayload? Payload { get; set; }
}

public class OpenserveResult
{
    public string? ResultCode { get; set; }
    public string? ResultMsgCode { get; set; }
    public string? ResultMsg { get; set; }
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
public sealed record OpenserveOrderFacts(
    string? Id,
    string? State,
    string? OrderName,
    string? ServiceOrderNumber,
    string? OrderDate);

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

    /// <summary>Reads id/state/OrderName/ServiceOrderNumber/orderDate out of a productOrder or cancelproductOrder JsonElement without binding the whole catalogue tree.</summary>
    public static OpenserveOrderFacts Extract(JsonElement order)
    {
        string? id = GetString(order, "id");
        string? state = GetString(order, "state");
        string? orderDate = GetString(order, "orderDate");
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

        return new OpenserveOrderFacts(id, state, orderName, serviceOrderNumber, orderDate);
    }

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

/// <summary>GET {BaseUrl}/{ws-ispcode}/productorder?...&searchKey=...&searchValue=... list response (spec §4.6.2).</summary>
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

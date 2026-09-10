using System.Text.Json.Serialization;

namespace SmartFuture.Application.Openserve.Dtos;

// Wire-format DTOs for the Openserve CREATE Product Order request,
// shaped exactly to the Fulfilment API Specification (ITSD-179559 Rev
// 04.002) §4.1 examples and §11 Appendix C. Serialized with
// JsonNamingPolicy.CamelCase (see OpenserveApiClient) so these C#
// PascalCase properties produce the documented camelCase wire field
// names (e.g. RequestedStartDate -> requestedStartDate) without needing
// [JsonPropertyName] on every property — only "@type" needs one, since
// camelCase can't produce a leading '@'.
//
// This models ONLY the "Sales Order" (new provide) shape needed for
// Phase 2 order submission — not the deeper catalogue/pricing tree
// used by GET/event responses (those are read with tolerant JsonElement
// extraction instead, see OpenserveWireResponseDtos.cs).
public class OpenserveCreateOrderRequest
{
    [JsonPropertyName("@type")]
    public string Type { get; set; } = "Sales Order";

    /// <summary>ISO-ish "yyyy-MM-ddTHH:mm:ss" per the spec's own examples.</summary>
    public string? RequestedStartDate { get; set; }

    public List<OpenserveComment>? Comments { get; set; }

    /// <summary>Only present for non-plain-Sales-Order flows (e.g. "Regrade", "Product Conversion", "Receive Ownership") — null for a first-time provide.</summary>
    public string? Reason { get; set; }

    public List<OpenserveProductOrderItem> ProductOrderItem { get; set; } = new();
}

public class OpenserveComment
{
    public string Comment { get; set; } = string.Empty;
}

public class OpenserveProductOrderItem
{
    /// <summary>"add" for a new provide; "replace" for Alter Product Options / Cancel Market Offer flows.</summary>
    public string Action { get; set; } = "add";

    public OpenserveOrderProduct Product { get; set; } = new();
}

public class OpenserveOrderProduct
{
    public OpenserveProductOffering? ProductOffering { get; set; }

    /// <summary>"External Reference Number" (mandatory) and "SKU" (mandatory) name/value pairs.</summary>
    public List<OpenserveNameValue> ProductCharacteristic { get; set; } = new();

    public List<OpenserveProductRelationship>? ProductRelationship { get; set; }

    public List<OpenserveRealizingService> RealizingService { get; set; } = new();
}

public class OpenserveProductOffering
{
    public string? Name { get; set; }
}

public class OpenserveProductRelationship
{
    public string RelationshipType { get; set; } = "childOffer";

    /// <summary>Always an empty object per every worked example in the spec.</summary>
    public object Product { get; set; } = new { };
}

public class OpenserveRealizingService
{
    /// <summary>Present for a new provide; omitted/empty for Regrade (spec: "place: []").</summary>
    public List<OpenservePlace>? Place { get; set; }

    /// <summary>Capacity, Capacity UOM, Circuit Number, Subscriber Reference Number, Subscriber Contact Name/Phone, ISP Identifier, etc.</summary>
    public List<OpenserveNameValue> ServiceCharacteristic { get; set; } = new();
}

public class OpenservePlace
{
    public string? BuildingName { get; set; }
    public string? Unit { get; set; }
    public string? Floor { get; set; }
    public string Street1 { get; set; } = string.Empty;
    public string? Suburb { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }

    /// <summary>Mandatory per Appendix C (1:N).</summary>
    public string Country { get; set; } = "South Africa";

    public string? Longitude { get; set; }
    public string? Latitude { get; set; }
    public string? BuildingNumId { get; set; }

    /// <summary>Address Master Identifier — mandatory per Appendix C (1:N). Never sent blank; the submission service blocks before building this DTO if AMID is missing.</summary>
    public string Amid { get; set; } = string.Empty;
}

public class OpenserveNameValue
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    public OpenserveNameValue() { }

    public OpenserveNameValue(string name, string value)
    {
        Name = name;
        Value = value;
    }
}

// POST {BaseUrl}/{ws-ispcode}/cancelproductorder body (spec §4.7).
public class OpenserveCancelOrderRequest
{
    public OpenserveOrderRef ProductOrder { get; set; } = new();
}

public class OpenserveOrderRef
{
    public string Id { get; set; } = string.Empty;
}

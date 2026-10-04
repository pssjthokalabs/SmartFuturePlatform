namespace SmartFuture.Application.Openserve;

/// <summary>
/// Verbatim transcription of Appendix D ("List of Products and Valid
/// Speeds") from the Openserve Fulfilment API Specification
/// (ITSD-179559 Rev 04.002, pp. 166–168): technology, product name, SKU,
/// capacity and capacity UOM, plus the "**" retention-offer marker.
///
/// This is the ONLY source of selectable Openserve values in the admin
/// mapping UI and the validation applied when a PackageOpenserveMapping is
/// saved. It never infers a SKU from a SmartFuture package's name or speed
/// — an admin chooses the Openserve product + speed explicitly; this only
/// guarantees the combination is one Openserve documents.
///
/// Neither the provisioned Postman collection nor the PDF offers a product
/// catalogue/offering endpoint: Product Qualification reports which
/// ProductCodes are available at an address and their maximum speeds, not
/// the orderable capacity tiers, so this appendix is the documented source
/// for tiers.
///
/// Retention offers ("**" in Appendix D: OFC 40 Mbps and OWS 40 Mbps Lite)
/// "may only be ordered as an Alter Product Option order, with reason
/// Regrade" — never as a new Sales Order — so they cannot be enabled for
/// new-order submission (see IsOrderableAsNewSalesOrder).
/// </summary>
public static class OpenserveProductCatalogue
{
    public const string Fibre = "FIBRE";
    public const string Copper = "COPPER";
    public const string Microwave = "MICROWAVE";
    public const string Satellite = "SATELLITE";

    /// <summary>productOffering.name per SKU, spelled exactly as Openserve documents it.</summary>
    public static readonly IReadOnlyDictionary<string, string> ProductNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["OCC"] = "Openserve Copper Connect",
        ["OFC"] = "Openserve Fibre Connect",
        ["OFCA"] = "Openserve Fibre Connect Air",
        ["OFCP"] = "Openserve Fibre Connect Premium",
        ["OFCPA"] = "Openserve Fibre Connect Premium Air",
        ["OOCF"] = "Openserve Office Connect",
        ["OOCA"] = "Openserve Office Connect Air",
        ["OPC"] = "Openserve Pure Connect",
        ["OSC"] = "Openserve Space Connect",
        ["OWC"] = "Openserve Web Connect",
        ["OWA"] = "Openserve Web Connect Air",
        ["OWCP"] = "Openserve Web Connect Prepaid",
        ["OWS"] = "Openserve Webstream",
        ["OWSP"] = "Openserve Webstream Prepaid",
        // Documented elsewhere in the spec, without an Appendix D speed table.
        ["OFTR"] = "Openserve Fibre To The Room",
        ["OIB"] = "Openserve Infrastructure Broadband",
        ["OMC"] = "Openserve MobiSat Connect",
        ["OFV"] = "Openserve Fibre Voice",
        ["OVC"] = "Openserve Voice Connect",
        ["OUC"] = "Openserve Uni Connect",
        // §4.1.15 Example 12 sends SKU "OWCA" for Web Connect Air; Appendix D
        // lists the same product as "OWA". Both kept, flagged for Openserve
        // to confirm — irrelevant to Fibre packages either way.
        ["OWCA"] = "Openserve Web Connect Air",
    };

    public static readonly IReadOnlyList<OpenserveCatalogueEntry> Entries = new List<OpenserveCatalogueEntry>
    {
        // Openserve Copper Connect (OCC) — COPPER
        new("OCC", "5", "Mbps", Copper), new("OCC", "10", "Mbps", Copper), new("OCC", "20", "Mbps", Copper), new("OCC", "40", "Mbps", Copper),

        // Openserve Fibre Connect (OFC) — FIBRE. ** 40 Mbps is a retention offer.
        new("OFC", "10", "Mbps Lite", Fibre), new("OFC", "30", "Mbps", Fibre), new("OFC", "40", "Mbps", Fibre, IsRetentionOffer: true),
        new("OFC", "50", "Mbps", Fibre), new("OFC", "50", "Mbps Lite", Fibre), new("OFC", "75", "Mbps", Fibre),
        new("OFC", "100", "Mbps", Fibre), new("OFC", "100", "Mbps Lite", Fibre), new("OFC", "200", "Mbps", Fibre),
        new("OFC", "200", "Mbps Lite", Fibre), new("OFC", "300", "Mbps Lite", Fibre), new("OFC", "500", "Mbps", Fibre),
        new("OFC", "1000", "Mbps Lite", Fibre),

        // Openserve Fibre Connect Air (OFCA) — MICROWAVE
        new("OFCA", "25", "Mbps", Microwave),

        // Openserve Fibre Connect Premium (OFCP) — FIBRE
        new("OFCP", "50", "Mbps", Fibre), new("OFCP", "100", "Mbps", Fibre), new("OFCP", "200", "Mbps", Fibre),
        new("OFCP", "300", "Mbps", Fibre), new("OFCP", "500", "Mbps", Fibre),

        // Openserve Fibre Connect Premium Air (OFCPA) — MICROWAVE
        new("OFCPA", "25", "Mbps", Microwave),

        // Openserve Office Connect (OOCF) — FIBRE
        new("OOCF", "50", "Mbps", Fibre), new("OOCF", "100", "Mbps", Fibre), new("OOCF", "200", "Mbps", Fibre),
        new("OOCF", "300", "Mbps", Fibre), new("OOCF", "500", "Mbps", Fibre), new("OOCF", "1000", "Mbps", Fibre),

        // Openserve Office Connect Air (OOCA) — MICROWAVE
        new("OOCA", "25", "Mbps", Microwave),

        // Openserve Pure Connect (OPC) — COPPER
        new("OPC", "5", "Mbps", Copper), new("OPC", "10", "Mbps", Copper), new("OPC", "20", "Mbps", Copper), new("OPC", "40", "Mbps", Copper),

        // Openserve Space Connect (OSC) — SATELLITE
        new("OSC", "5", "Mbps", Satellite), new("OSC", "10", "Mbps", Satellite), new("OSC", "20", "Mbps", Satellite), new("OSC", "50", "Mbps", Satellite),

        // Openserve Web Connect (OWC) — FIBRE
        new("OWC", "10", "Mbps", Fibre), new("OWC", "20", "Mbps", Fibre), new("OWC", "40", "Mbps", Fibre),

        // Openserve Web Connect Air (OWA) — MICROWAVE
        new("OWA", "20", "Mbps", Microwave), new("OWA", "30", "Mbps", Microwave),

        // Openserve Web Connect Prepaid (OWCP) — FIBRE
        new("OWCP", "20", "Mbps", Fibre),

        // Openserve Webstream (OWS) — FIBRE. ** 40 Mbps Lite is a retention offer.
        new("OWS", "10", "Mbps", Fibre), new("OWS", "25", "Mbps", Fibre), new("OWS", "40", "Mbps Lite", Fibre, IsRetentionOffer: true),
        new("OWS", "50", "Mbps", Fibre), new("OWS", "75", "Mbps Lite", Fibre), new("OWS", "100", "Mbps", Fibre),
        new("OWS", "200", "Mbps", Fibre), new("OWS", "300", "Mbps", Fibre), new("OWS", "500", "Mbps", Fibre), new("OWS", "1000", "Mbps", Fibre),

        // Openserve Webstream Prepaid (OWSP) — FIBRE
        new("OWSP", "50", "Mbps", Fibre),
    };

    // Referenced elsewhere in the spec (order examples, qualification
    // sample, Appendix E migrations) but NOT given a speed table in
    // Appendix D — only the SKU is known to be real; no capacity
    // validation is possible for these.
    public static readonly IReadOnlySet<string> SkusWithoutPublishedSpeedTable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "OFTR", "OIB", "OMC", "OFV", "OVC", "OUC", "OWCA",
    };

    public static bool IsKnownSku(string sku) =>
        Entries.Any(e => string.Equals(e.Sku, sku, StringComparison.OrdinalIgnoreCase))
        || SkusWithoutPublishedSpeedTable.Contains(sku);

    public static bool IsValidCombination(string sku, string capacity, string capacityUom) => Find(sku, capacity, capacityUom) is not null;

    public static bool IsRetentionOffer(string sku, string capacity, string capacityUom) => Find(sku, capacity, capacityUom)?.IsRetentionOffer == true;

    /// <summary>True when a new Sales Order (the only order type SmartFuture submits) may use this combination.</summary>
    public static bool IsOrderableAsNewSalesOrder(string sku, string capacity, string capacityUom)
        => SkusWithoutPublishedSpeedTable.Contains(sku) || (IsValidCombination(sku, capacity, capacityUom) && !IsRetentionOffer(sku, capacity, capacityUom));

    public static string? ProductNameFor(string sku) => ProductNames.TryGetValue(sku, out var name) ? name : null;

    public static string? TechnologyFor(string sku) => Entries.FirstOrDefault(e => string.Equals(e.Sku, sku, StringComparison.OrdinalIgnoreCase))?.Technology;

    private static OpenserveCatalogueEntry? Find(string sku, string capacity, string capacityUom) =>
        Entries.FirstOrDefault(e =>
            string.Equals(e.Sku, sku?.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Capacity, capacity?.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.CapacityUom, capacityUom?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public record OpenserveCatalogueEntry(string Sku, string Capacity, string CapacityUom, string Technology, bool IsRetentionOffer = false);

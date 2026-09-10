namespace SmartFuture.Application.Openserve;

/// <summary>
/// Verbatim transcription of Appendix D ("List of Products and Valid
/// Speeds") from the Openserve Fulfilment API Specification
/// (ITSD-179559 Rev 04.002). Used to validate a
/// PackageOpenserveMapping's Sku/Capacity/CapacityUom against the
/// documented catalogue at save time, so a typo can't silently create
/// a mapping Openserve would reject — this does NOT infer a SKU from
/// the SmartFuture package name (brief §4 explicitly forbids that);
/// admin still chooses the SKU/capacity explicitly, this only checks
/// the combination is one Openserve actually documents.
///
/// Retention-offer speeds (marked ** in Appendix D — currently only
/// OWS/25 Mbps) are intentionally included here: the spec says they
/// cannot be used for new Sales Orders, only for a downgrade Regrade
/// from the tier above. That business rule belongs to the order-
/// submission logic (Phase 2), not to catalogue validation — a mapping
/// row for a retention speed is still a legitimate thing for admin to
/// pre-configure.
/// </summary>
public static class OpenserveProductCatalogue
{
    public static readonly IReadOnlyList<OpenserveCatalogueEntry> Entries = new List<OpenserveCatalogueEntry>
    {
        // Openserve Copper Connect (OCC)
        new("OCC", "5", "Mbps"), new("OCC", "10", "Mbps"), new("OCC", "20", "Mbps"), new("OCC", "40", "Mbps"),

        // Openserve Fibre Connect (OFC)
        new("OFC", "10", "Mbps Lite"), new("OFC", "30", "Mbps"), new("OFC", "40", "Mbps"),
        new("OFC", "50", "Mbps"), new("OFC", "50", "Mbps Lite"), new("OFC", "75", "Mbps"),
        new("OFC", "100", "Mbps"), new("OFC", "100", "Mbps Lite"), new("OFC", "200", "Mbps"),
        new("OFC", "200", "Mbps Lite"), new("OFC", "300", "Mbps Lite"), new("OFC", "500", "Mbps"),
        new("OFC", "1000", "Mbps Lite"),

        // Openserve Fibre Connect Air (OFCA)
        new("OFCA", "25", "Mbps"),

        // Openserve Fibre Connect Premium (OFCP)
        new("OFCP", "50", "Mbps"), new("OFCP", "100", "Mbps"), new("OFCP", "200", "Mbps"),
        new("OFCP", "300", "Mbps"), new("OFCP", "500", "Mbps"),

        // Openserve Fibre Connect Premium Air (OFCPA)
        new("OFCPA", "25", "Mbps"),

        // Openserve Office Connect (OOCF)
        new("OOCF", "50", "Mbps"), new("OOCF", "100", "Mbps"), new("OOCF", "200", "Mbps"),
        new("OOCF", "300", "Mbps"), new("OOCF", "500", "Mbps"), new("OOCF", "1000", "Mbps"),

        // Openserve Office Connect Air (OOCA)
        new("OOCA", "25", "Mbps"),

        // Openserve Pure Connect (OPC)
        new("OPC", "5", "Mbps"), new("OPC", "10", "Mbps"), new("OPC", "20", "Mbps"), new("OPC", "40", "Mbps"),

        // Openserve Space Connect (OSC)
        new("OSC", "5", "Mbps"), new("OSC", "10", "Mbps"), new("OSC", "20", "Mbps"), new("OSC", "50", "Mbps"),

        // Openserve Web Connect (OWC)
        new("OWC", "10", "Mbps"), new("OWC", "20", "Mbps"), new("OWC", "40", "Mbps"),

        // Openserve Web Connect Air (OWCA)
        new("OWCA", "20", "Mbps"), new("OWCA", "30", "Mbps"),

        // Openserve Web Connect Prepaid (OWCP)
        new("OWCP", "20", "Mbps"),

        // Openserve Webstream (OWS)
        new("OWS", "10", "Mbps"), new("OWS", "25", "Mbps", IsRetentionOffer: true), new("OWS", "40", "Mbps Lite"),
        new("OWS", "50", "Mbps"), new("OWS", "75", "Mbps Lite"), new("OWS", "100", "Mbps"),
        new("OWS", "200", "Mbps"), new("OWS", "300", "Mbps"), new("OWS", "500", "Mbps"), new("OWS", "1000", "Mbps"),

        // Openserve Webstream Prepaid (OWSP)
        new("OWSP", "50", "Mbps"),
    };

    // Referenced elsewhere in the spec (order examples, Appendix E migrations)
    // but NOT given a speed table in Appendix D — no capacity validation is
    // possible for these, only the SKU is known to be real.
    public static readonly IReadOnlySet<string> SkusWithoutPublishedSpeedTable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "OFTR", // Openserve Fibre To The Room
        "OIB",  // Openserve Infrastructure Broadband
        "OMC",  // Openserve MobiSat Connect
        "OFV",  // Openserve Fibre Voice
        "OVC",  // Openserve Voice Connect
        "OUC",  // Openserve Uni Connect
    };

    public static bool IsKnownSku(string sku) =>
        Entries.Any(e => string.Equals(e.Sku, sku, StringComparison.OrdinalIgnoreCase))
        || SkusWithoutPublishedSpeedTable.Contains(sku);

    public static bool IsValidCombination(string sku, string capacity, string capacityUom) =>
        Entries.Any(e =>
            string.Equals(e.Sku, sku, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Capacity, capacity, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.CapacityUom, capacityUom, StringComparison.OrdinalIgnoreCase));
}

public record OpenserveCatalogueEntry(string Sku, string Capacity, string CapacityUom, bool IsRetentionOffer = false);

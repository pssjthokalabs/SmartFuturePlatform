namespace SmartFuture.Shared.Enums.Openserve;

// How the Openserve premises (AMID) for a customer's address was
// established. Openserve's Address Master and Google/municipal addresses
// can disagree even at the same coordinates, so a coordinate lookup only
// yields CANDIDATES (Product Qualification with FORCEVERIFY=Y →
// AddressVerify[]); the AMID used for qualification and ordering must be
// one that matched the customer's address, or one the customer or an
// Admin explicitly chose from that list. The chosen record is the Openserve
// SERVICE PREMISES — it never replaces the customer's installation address.
public enum OpenserveAddressResolution
{
    /// <summary>Not recorded — evidence written before address verification existed (the AMID came from the nearest-address lookup).</summary>
    NotEvaluated = 0,

    /// <summary>Exactly one AddressVerify candidate matched the customer's street number and street; its AMID was qualified.</summary>
    AutoMatched = 1,

    /// <summary>An Admin explicitly chose one of the AddressVerify candidates (audited); its AMID was qualified.</summary>
    AdminSelected = 2,

    /// <summary>Openserve returned nearby candidates, but none (or more than one) safely matched the customer's address — no AMID chosen.</summary>
    Unresolved = 3,

    /// <summary>Openserve returned no address candidates near the customer's location.</summary>
    NoCandidates = 4,

    /// <summary>
    /// No candidate matched automatically and the CUSTOMER chose one of the AddressVerify
    /// candidates as the Openserve service location of their property, with explicit
    /// confirmation (audited); its AMID was qualified. Their installation address is unchanged.
    /// </summary>
    CustomerSelected = 5
}

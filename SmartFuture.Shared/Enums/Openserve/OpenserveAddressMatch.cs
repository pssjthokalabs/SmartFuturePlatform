namespace SmartFuture.Shared.Enums.Openserve;

// Whether the address Openserve resolved for an AMID is the customer's
// property. Qualification runs on coordinates, so Openserve answers with
// the NEAREST address it knows — which can be a neighbour's.
public enum OpenserveAddressMatch
{
    NotEvaluated = 0,

    /// <summary>Street number and street name agree.</summary>
    Matched = 1,

    /// <summary>Not enough comparable detail to be sure (e.g. a street number on one side only) — must be confirmed before ordering.</summary>
    ReviewRequired = 2,

    /// <summary>Openserve resolved a different property (different street number or street).</summary>
    Mismatch = 3
}

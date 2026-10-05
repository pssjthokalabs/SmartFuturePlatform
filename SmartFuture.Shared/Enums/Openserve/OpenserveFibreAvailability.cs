namespace SmartFuture.Shared.Enums.Openserve;

// What Openserve Product Qualification said about FTTH at the address
// (spec §3.1.1.4 FTTH_Status). An AMID only identifies the address — it
// says nothing about Fibre; this does.
public enum OpenserveFibreAvailability
{
    /// <summary>No qualification evidence evaluated (never ran, or the call failed).</summary>
    NotEvaluated = 0,

    /// <summary>At least one FTTH infrastructure is "Working"/"Available" — immediately available per the spec.</summary>
    Available = 1,

    /// <summary>FTTH infrastructure was returned, but none is immediately available (e.g. "Future (Planned)", "Pre Order", "pending").</summary>
    NotYetAvailable = 2,

    /// <summary>The response carried no FTTH infrastructure at all — Fibre is not available at this address.</summary>
    NotReturned = 3
}

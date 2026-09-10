using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Application.Openserve;

/// <summary>
/// Raw Openserve order-state string (Fulfilment API Spec Appendix A) ->
/// our own OpenserveProvisioningStatus. Pure, isolated, and unit-tested
/// on its own — this is the exact mapping referenced in the brief's
/// "raw → normalized status mapping" report item.
///
/// Case/whitespace-insensitive. An unrecognised value maps to Unknown
/// rather than throwing — brief §8: "Unknown Openserve state must be
/// stored rather than causing failure."
/// </summary>
public static class OpenserveStatusNormalizer
{
    public static OpenserveProvisioningStatus Normalize(string? rawState)
    {
        if (string.IsNullOrWhiteSpace(rawState)) return OpenserveProvisioningStatus.Unknown;

        return rawState.Trim().ToLowerInvariant() switch
        {
            "pending" => OpenserveProvisioningStatus.Submitted,
            "validated" => OpenserveProvisioningStatus.Submitted,
            "acknowledged" => OpenserveProvisioningStatus.Submitted,
            "in progress" => OpenserveProvisioningStatus.InProgress,
            "assessing cancellation" => OpenserveProvisioningStatus.AwaitingCancellation,
            "pending cancellation" => OpenserveProvisioningStatus.AwaitingCancellation,
            "cancelled" => OpenserveProvisioningStatus.Cancelled,
            "canceled" => OpenserveProvisioningStatus.Cancelled, // tolerate US spelling in case a future payload varies
            "accepted" => OpenserveProvisioningStatus.Completed,
            // "Held" is mentioned only in Appendix A's prose (a possible
            // revert target during cancellation assessment), never
            // listed as a top-level state — treat as in-flight rather
            // than terminal until Openserve confirms it's real.
            "held" => OpenserveProvisioningStatus.InProgress,
            _ => OpenserveProvisioningStatus.Unknown
        };
    }

    public static bool IsTerminal(OpenserveProvisioningStatus status)
        => status is OpenserveProvisioningStatus.Completed or OpenserveProvisioningStatus.Cancelled;
}

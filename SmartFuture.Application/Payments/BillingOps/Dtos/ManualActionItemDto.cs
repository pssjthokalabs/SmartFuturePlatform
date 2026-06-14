namespace SmartFuture.Application.Payments.BillingOps.Dtos;

/// <summary>
/// One bucket in the Billing Ops attention list. <see cref="Key"/> is a
/// stable machine token the portal can route on; <see cref="Count"/> is the
/// number of items needing attention; <see cref="Severity"/> drives the
/// badge colour ("info" | "warning" | "critical").
/// </summary>
public sealed class ManualActionItemDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
    public string Severity { get; set; } = "info";
    /// <summary>Optional portal link the operator can follow to act on the bucket.</summary>
    public string? Link { get; set; }
}

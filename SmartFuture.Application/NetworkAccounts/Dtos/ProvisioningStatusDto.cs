namespace SmartFuture.Application.NetworkAccounts.Dtos;

// Phase 3.6 — runtime state surfaced to the portal so it can render
// the right banner + decide whether to enable action buttons without
// having to introspect config or wait for a 503 round-trip. Returned
// by GET /api/provisioning/status.
public class ProvisioningStatusDto
{
    /// <summary>True when admins are permitted to invoke manual actions.</summary>
    public bool Enabled { get; set; }

    /// <summary>Active mode: NoOp | Radius | MikroTik | Hybrid.</summary>
    public string Mode { get; set; } = "NoOp";

    /// <summary>
    /// True when the backend will accept action POSTs. Currently
    /// requires Enabled AND Mode == "NoOp" until real providers land.
    /// </summary>
    public bool IsActionable { get; set; }

    /// <summary>
    /// Stable, user-presentable label the portal can show in banners.
    /// Always reflects current state (e.g. "Disabled", "Simulation —
    /// no live router yet").
    /// </summary>
    public string StatusLabel { get; set; } = string.Empty;
}

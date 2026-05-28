namespace SmartFuture.Infrastructure.Configuration;

// Phase 3.6 — runtime configuration for the RADIUS / network
// provisioning subsystem. The committed appsettings.json carries a
// safe default of Enabled=false + Mode=NoOp so a fresh deploy can
// never accidentally pretend a real provider is wired up.
//
// Env-var keys (double-underscore convention):
//
//   Provisioning__Enabled=false           // master kill switch
//   Provisioning__Mode=NoOp               // NoOp | Radius | MikroTik | Hybrid
//
// Read by:
//   - NoOpNetworkProvisioningService — gate every action call
//   - ProvisioningStatusController     — surfaces state to the portal
//
// Until a real provider lands, only the NoOp mode is honoured. The
// other enum values exist so the contract is stable when Phase 4
// adds Radius / MikroTik / Hybrid implementations.
public class ProvisioningSettings
{
    public const string SectionName = "Provisioning";

    /// <summary>
    /// Master kill switch. When false, every provisioning action
    /// returns a clear "currently disabled" error regardless of mode.
    /// Production default: false.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Active provisioning mode. Only "NoOp" is currently honoured.
    /// Real provider modes (Radius / MikroTik / Hybrid) are reserved
    /// for Phase 4 — selecting one today still routes through the
    /// NoOp stub but the status endpoint reports the configured value
    /// so the portal can warn appropriately.
    /// </summary>
    public ProvisioningMode Mode { get; set; } = ProvisioningMode.NoOp;

    /// <summary>True when actions may run against the NoOp stub.</summary>
    public bool IsNoOpActionable => Enabled && Mode == ProvisioningMode.NoOp;
}

public enum ProvisioningMode
{
    NoOp = 0,
    Radius = 1,
    MikroTik = 2,
    Hybrid = 3
}

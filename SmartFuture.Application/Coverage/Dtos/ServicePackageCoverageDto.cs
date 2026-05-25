using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Application.Coverage.Dtos;

// Customer-safe projection of one SmartFuture ServicePackage that is
// orderable at the coverage-checked address. The website renders the
// `AvailablePackages` array directly so it doesn't have to fetch
// /api/service-packages/public separately or re-implement the
// Openserve-vs-package matching rules on the client.
//
// MatchReason is an optional short string the matcher fills in so the
// UI can show why this package was offered (e.g. "Within Openserve
// 1000 Mbps line capability"). It is intentionally null when no
// meaningful reason can be conveyed.
public class ServicePackageCoverageDto
{
    public Guid                        Id                  { get; set; }
    public string                      Name                { get; set; } = string.Empty;
    public string?                     ShortDescription    { get; set; }
    public string?                     SpeedLabel          { get; set; }
    public int?                        DownloadSpeedMbps   { get; set; }
    public int?                        UploadSpeedMbps     { get; set; }
    public decimal                     Price               { get; set; }
    public ServicePackageBillingCycle  BillingCycle        { get; set; }
    public bool                        HasFreeInstallation { get; set; }
    public decimal?                    InstallationFee     { get; set; }
    public bool                        IncludesRouter      { get; set; }
    public string?                     RouterDescription   { get; set; }
    public string?                     TermsSummary        { get; set; }
    public string?                     ExternalReference   { get; set; }
    public bool                        IsFeatured          { get; set; }
    public int                         DisplayOrder        { get; set; }
    public string?                     MatchReason         { get; set; }
}

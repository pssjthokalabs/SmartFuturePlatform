namespace SmartFuture.Infrastructure.Configuration;

// Runtime seed configuration for the initial Smart Future service
// packages (Openserve fibre rows).
//
//   PackageSeed__Enabled=true
//   PackageSeed__UpdateExisting=false
//
// Idempotency:
//  - The seeder looks up existing rows by `ExternalReference` (a stable
//    key composed from provider+type+download+upload, e.g. "openserve-fibre-100-50").
//  - If a row exists and `UpdateExisting=false` (default) it is left
//    alone — admins can adjust pricing or features through the portal
//    without the seeder clobbering changes on the next deploy.
//  - If `UpdateExisting=true`, name/price/feature fields on the matched
//    row are overwritten from the seed payload. Useful for UAT resets.
public class PackageSeedSettings
{
    public const string SectionName = "PackageSeed";

    public bool Enabled { get; set; }
    public bool UpdateExisting { get; set; }
}

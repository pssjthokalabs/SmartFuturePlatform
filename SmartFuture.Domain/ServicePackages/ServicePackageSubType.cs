using SmartFuture.Domain.Common;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Domain.ServicePackages;

// DB-driven sub-category for a service package line. The first consumer
// is Security/CCTV, which needs to distinguish CCTV vs Intercom (and more
// later) on the public site's breadcrumb + filters. Kept generic
// (scoped by PackageType) so any other line can grow subtypes without a
// new table.
public class ServicePackageSubType : BaseEntity
{
    // Which package line this subtype belongs to (Security, Fibre, …).
    // The admin subtype dropdown is filtered by this so a Security
    // package only ever offers Security subtypes.
    public ServicePackageType PackageType { get; set; }

    // Human-facing name shown in the admin dropdown + public breadcrumb
    // (e.g. "CCTV", "Intercom").
    public string Name { get; set; } = string.Empty;

    // URL/style-safe identifier (e.g. "cctv", "intercom"). Unique per
    // PackageType so the public site can key off it later for filters.
    public string Slug { get; set; } = string.Empty;

    // Soft-disable: an inactive subtype stays selectable on packages that
    // already reference it (so historical rows still render) but is hidden
    // from the "new package" dropdown.
    public bool IsActive { get; set; } = true;

    public int DisplayOrder { get; set; }
}

using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Queries;

namespace SmartFuture.Application.ServicePackages.Dtos;

public class ServicePackageFilterRequestDto : PagedListQueryBase
{
    // Numeric service-package type. ASP.NET Core's enum binder accepts
    // BOTH the integer form (?type=6) AND the enum name (?type=Security,
    // case-insensitive), so this single property already covers two of
    // the three accepted query styles.
    public ServicePackageType? Type { get; set; }

    // Frontend-friendly alias — the portal/mobile/website may send
    // ?serviceType=Security or ?serviceType=Fibre instead of the
    // backend's ?type= name. Resolved in the service layer via
    // `ResolveEffectiveType()`; when both are set, `Type` wins.
    public string? ServiceType { get; set; }

    public ServicePackageStatus? StatusFilter { get; set; }
    public bool? IsFeatured { get; set; }
    public bool? IsUncapped { get; set; }
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }

    // Resolve the effective filter type from either query alias.
    // Returns null when neither is set OR when the string alias is
    // unrecognised (which means "no type filter", not "match nothing"
    // — that matches the existing behaviour when `Type` is null).
    public ServicePackageType? ResolveEffectiveType()
    {
        if (Type.HasValue) return Type;
        if (string.IsNullOrWhiteSpace(ServiceType)) return null;

        // Accept the enum name (Fibre / Security / etc., case-insensitive)
        // AND the integer form for symmetry with ?type=. Anything else
        // falls through to "no filter" — the caller's filter is silently
        // dropped rather than 500ing on a typo.
        if (Enum.TryParse<ServicePackageType>(ServiceType, ignoreCase: true, out var parsed))
            return parsed;

        return null;
    }
}

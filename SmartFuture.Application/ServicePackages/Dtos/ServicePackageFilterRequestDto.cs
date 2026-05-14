using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Queries;

namespace SmartFuture.Application.ServicePackages.Dtos;

public class ServicePackageFilterRequestDto : PagedListQueryBase
{
    public ServicePackageType? Type { get; set; }
    public new ServicePackageStatus? Status { get; set; }
    public bool? IsFeatured { get; set; }
    public bool? IsUncapped { get; set; }
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
}

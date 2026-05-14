using SmartFuture.Shared.Enums.CoverageRequests;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Queries;

namespace SmartFuture.Application.CoverageRequests.Dtos;

public class CoverageRequestFilterRequestDto : PagedListQueryBase
{
    public Guid? UserId { get; set; }
    public Guid? CustomerProfileId { get; set; }
    public Guid? ServicePackageId { get; set; }
    public ServicePackageType? RequestedServiceType { get; set; }
    public new CoverageRequestStatus? Status { get; set; }
    public CoverageRequestSource? Source { get; set; }

    public string? City { get; set; }
    public string? Suburb { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }

    public DateTime? ReviewedFromUtc { get; set; }
    public DateTime? ReviewedToUtc { get; set; }
}

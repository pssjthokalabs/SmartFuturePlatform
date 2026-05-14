using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Queries;

namespace SmartFuture.Application.Orders.Dtos;

public class OrderFilterRequestDto : PagedListQueryBase
{
    public Guid? UserId { get; set; }
    public Guid? CustomerProfileId { get; set; }
    public Guid? ServicePackageId { get; set; }
    public Guid? CoverageRequestId { get; set; }
    public OrderStatus? StatusFilter { get; set; }
    public OrderSource? Source { get; set; }
    public ServicePackageType? PackageType { get; set; }

    public string? City { get; set; }
    public string? Suburb { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }

    public DateTime? SubmittedFromUtc { get; set; }
    public DateTime? SubmittedToUtc { get; set; }
    public DateTime? ExpectedInstallationFromUtc { get; set; }
    public DateTime? ExpectedInstallationToUtc { get; set; }
}

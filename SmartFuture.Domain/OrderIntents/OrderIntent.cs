using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;
using SmartFuture.Domain.Orders;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.OrderIntents;

namespace SmartFuture.Domain.OrderIntents;

// Public pre-order intent. Created anonymously by the marketing site
// when a visitor clicks "Get Started" on a fibre package, captures the
// fields needed to seed a real order, and is later "claimed" + "converted"
// by an authenticated client in the portal. The intent NEVER becomes the
// order itself — see OrderIntentService.ConvertAsync, which calls the
// existing IOrderService.CreateMineAsync so backend pricing/snapshots
// remain authoritative.
public class OrderIntent : BaseEntity
{
    public string IntentToken { get; set; } = string.Empty;

    public Guid ServicePackageId { get; set; }
    public ServicePackage? ServicePackage { get; set; }

    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }

    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? Suburb { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }

    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string? GooglePlaceId { get; set; }
    public string? MapProviderReference { get; set; }

    public DateTime? RequestedInstallationDateUtc { get; set; }
    public string? CustomerNotes { get; set; }

    public OrderIntentStatus Status { get; set; } = OrderIntentStatus.Pending;

    public Guid? ClaimedByUserId { get; set; }
    public User? ClaimedByUser { get; set; }

    public Guid? ConvertedOrderId { get; set; }
    public Order? ConvertedOrder { get; set; }

    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ClaimedAtUtc { get; set; }
    public DateTime? ConvertedAtUtc { get; set; }

    public string? Source { get; set; }
}

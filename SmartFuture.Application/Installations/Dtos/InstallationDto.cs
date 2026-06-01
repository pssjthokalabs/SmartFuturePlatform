using SmartFuture.Shared.Enums.Installations;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Application.Installations.Dtos;

public class InstallationDto
{
    public Guid Id { get; set; }
    public string InstallationNumber { get; set; } = string.Empty;

    public Guid OrderId { get; set; }
    public string? OrderNumber { get; set; }
    public OrderStatus? OrderStatus { get; set; }

    // Phase 45 — customer + package snapshot pulled from the linked
    // order at read time. The installation row itself doesn't store
    // these (they live on `Order`), so the API surfaces them via this
    // DTO so the admin Installation detail page doesn't render empty
    // "Customer / Package" cards.
    public Guid? CustomerUserId { get; set; }
    public string? CustomerFullName { get; set; }
    public string? CustomerEmail { get; set; }
    public string? CustomerPhoneNumber { get; set; }

    public string? PackageName { get; set; }
    public ServicePackageType? PackageType { get; set; }
    public string? PackageSpeedLabel { get; set; }
    public decimal? PackagePrice { get; set; }

    public InstallationStatus Status { get; set; }
    public InstallationSource Source { get; set; }

    public DateTime? ScheduledForUtc { get; set; }
    public DateTime? RescheduledFromUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public DateTime? FailedAtUtc { get; set; }

    public string? TechnicianName { get; set; }
    public string? TechnicianPhone { get; set; }
    public string? TechnicianEmail { get; set; }
    public Guid? TechnicianUserId { get; set; }

    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string? Suburb { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string? GooglePlaceId { get; set; }

    public string? CustomerNotes { get; set; }
    public string? AdminNotes { get; set; }
    public string? TechnicianNotes { get; set; }
    public string? CompletionNotes { get; set; }
    public string? FailureReason { get; set; }
    public string? CancellationReason { get; set; }

    // Technician completion details (go-live alignment).
    public string? RouterMakeModel { get; set; }
    public string? RouterSerialNumber { get; set; }
    public string? RouterMacAddress { get; set; }
    public string? OntReference { get; set; }
    public string? InstalledLocationNotes { get; set; }
    public string? SpeedTestResult { get; set; }
    public string? CustomerSignOffName { get; set; }

    public Guid? LastStatusChangedByUserId { get; set; }
    public string? LastStatusChangedByUserEmail { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>
    /// Populated only on the response returned from
    /// <c>AdminUpdateStatusAsync</c> when the new status is
    /// <c>Completed</c>. Carries the result of the first monthly invoice
    /// + auto-debit attempt so the admin portal can render a single
    /// success / warning modal.
    /// </summary>
    public InstallationCompletionBillingOutcomeDto? BillingOutcome { get; set; }
}

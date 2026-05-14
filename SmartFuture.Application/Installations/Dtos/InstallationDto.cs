using SmartFuture.Shared.Enums.Installations;
using SmartFuture.Shared.Enums.Orders;

namespace SmartFuture.Application.Installations.Dtos;

public class InstallationDto
{
    public Guid Id { get; set; }
    public string InstallationNumber { get; set; } = string.Empty;

    public Guid OrderId { get; set; }
    public string? OrderNumber { get; set; }
    public OrderStatus? OrderStatus { get; set; }

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

    public Guid? LastStatusChangedByUserId { get; set; }
    public string? LastStatusChangedByUserEmail { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

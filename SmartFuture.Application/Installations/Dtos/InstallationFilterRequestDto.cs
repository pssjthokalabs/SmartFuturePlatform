using SmartFuture.Shared.Enums.Installations;
using SmartFuture.Shared.Queries;

namespace SmartFuture.Application.Installations.Dtos;

public class InstallationFilterRequestDto : PagedListQueryBase
{
    public Guid? OrderId { get; set; }
    public string? OrderNumber { get; set; }
    public InstallationStatus? StatusFilter { get; set; }
    public InstallationSource? Source { get; set; }
    public Guid? TechnicianUserId { get; set; }
    public string? TechnicianName { get; set; }

    public string? City { get; set; }
    public string? Suburb { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }

    public DateTime? ScheduledFromUtc { get; set; }
    public DateTime? ScheduledToUtc { get; set; }
    public DateTime? CompletedFromUtc { get; set; }
    public DateTime? CompletedToUtc { get; set; }
}

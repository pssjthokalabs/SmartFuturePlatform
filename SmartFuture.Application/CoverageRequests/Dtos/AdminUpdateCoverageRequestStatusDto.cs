using SmartFuture.Shared.Enums.CoverageRequests;

namespace SmartFuture.Application.CoverageRequests.Dtos;

public class AdminUpdateCoverageRequestStatusDto
{
    public CoverageRequestStatus Status { get; set; }
    public string? AdminNotes { get; set; }
    public string? CoverageResultSummary { get; set; }
}

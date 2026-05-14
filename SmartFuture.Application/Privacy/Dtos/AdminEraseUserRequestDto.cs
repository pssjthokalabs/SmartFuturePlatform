namespace SmartFuture.Application.Privacy.Dtos;

public class AdminEraseUserRequestDto
{
    public Guid UserId { get; set; }
    public string? AdminReason { get; set; }
    public bool ConfirmErasePersonalData { get; set; }
    public bool AlsoCloseOpenSupportTickets { get; set; }
    public bool AlsoCancelPendingCoverageRequests { get; set; }
    public bool AlsoCancelDraftOrSubmittedOrders { get; set; }
}

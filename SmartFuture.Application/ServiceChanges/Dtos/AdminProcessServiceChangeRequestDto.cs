namespace SmartFuture.Application.ServiceChanges.Dtos;

// Admin action body. Single endpoint per outcome — keeps the audit
// trail tidy and lets the controller authorise each verb separately.
public class AdminProcessServiceChangeRequestDto
{
    public string? AdminNotes { get; set; }
}

public class AdminRejectServiceChangeRequestDto
{
    public string? RejectionReason { get; set; }
    public string? AdminNotes      { get; set; }
}

public class AdminCancelServiceChangeRequestDto
{
    public string? CancellationReason { get; set; }
    public string? AdminNotes         { get; set; }
}

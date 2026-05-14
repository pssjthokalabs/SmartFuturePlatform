namespace SmartFuture.Application.Privacy.Dtos;

public class UserErasureResultDto
{
    public Guid UserId { get; set; }
    public bool Success { get; set; }
    public DateTime ErasedAtUtc { get; set; }

    public int CustomerProfilesUpdated { get; set; }
    public int CoverageRequestsAnonymised { get; set; }
    public int OrdersAnonymised { get; set; }
    public int InstallationsAnonymised { get; set; }
    public int SupportTicketsAnonymised { get; set; }
    public int SupportTicketCommentsAnonymised { get; set; }
    public int OutboundNotificationsAnonymised { get; set; }
    public int NetworkAccountsTerminated { get; set; }
    public int AuditLogsTouched { get; set; }

    public string Message { get; set; } = string.Empty;
}

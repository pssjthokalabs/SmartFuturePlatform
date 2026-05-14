using SmartFuture.Domain.Billing;
using SmartFuture.Domain.Common;
using SmartFuture.Domain.CoverageRequests;
using SmartFuture.Domain.Identity;
using SmartFuture.Domain.Installations;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.SupportTickets;

namespace SmartFuture.Domain.SupportTickets;

public class SupportTicket : BaseEntity
{
    public string TicketNumber { get; set; } = string.Empty;

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid? OrderId { get; set; }
    public Order? Order { get; set; }

    public Guid? CoverageRequestId { get; set; }
    public CoverageRequest? CoverageRequest { get; set; }

    public Guid? InstallationId { get; set; }
    public Installation? Installation { get; set; }

    public Guid? InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    public Guid? PaymentId { get; set; }
    public Payment? Payment { get; set; }

    public Guid? DebitOrderMandateId { get; set; }
    public DebitOrderMandate? DebitOrderMandate { get; set; }

    public SupportTicketStatus Status { get; set; } = SupportTicketStatus.Open;
    public SupportTicketCategory Category { get; set; } = SupportTicketCategory.General;
    public SupportTicketPriority Priority { get; set; } = SupportTicketPriority.Normal;
    public SupportTicketSource Source { get; set; } = SupportTicketSource.CustomerApp;

    public string Subject { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public Guid? AssignedToUserId { get; set; }
    public User? AssignedToUser { get; set; }

    public DateTime? FirstRespondedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public DateTime? ReopenedAtUtc { get; set; }

    public Guid? LastStatusChangedByUserId { get; set; }
    public User? LastStatusChangedByUser { get; set; }

    public string? InternalSummary { get; set; }
    public string? ResolutionSummary { get; set; }
}

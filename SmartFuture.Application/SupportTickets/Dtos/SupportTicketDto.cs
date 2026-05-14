using SmartFuture.Shared.Enums.SupportTickets;

namespace SmartFuture.Application.SupportTickets.Dtos;

public class SupportTicketDto
{
    public Guid Id { get; set; }
    public string TicketNumber { get; set; } = string.Empty;

    public Guid UserId { get; set; }
    public string? UserEmail { get; set; }

    public Guid? OrderId { get; set; }
    public string? OrderNumber { get; set; }
    public Guid? CoverageRequestId { get; set; }
    public Guid? InstallationId { get; set; }
    public string? InstallationNumber { get; set; }
    public Guid? InvoiceId { get; set; }
    public string? InvoiceNumber { get; set; }
    public Guid? PaymentId { get; set; }
    public string? PaymentNumber { get; set; }
    public Guid? DebitOrderMandateId { get; set; }

    public SupportTicketStatus Status { get; set; }
    public SupportTicketCategory Category { get; set; }
    public SupportTicketPriority Priority { get; set; }
    public SupportTicketSource Source { get; set; }

    public string Subject { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public Guid? AssignedToUserId { get; set; }
    public string? AssignedToUserEmail { get; set; }

    public DateTime? FirstRespondedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public DateTime? ReopenedAtUtc { get; set; }

    public Guid? LastStatusChangedByUserId { get; set; }
    public string? LastStatusChangedByUserEmail { get; set; }

    public string? InternalSummary { get; set; }
    public string? ResolutionSummary { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

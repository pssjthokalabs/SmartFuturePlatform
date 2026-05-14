using SmartFuture.Shared.Enums.SupportTickets;

namespace SmartFuture.Application.SupportTickets.Dtos;

public class CreateSupportTicketRequestDto
{
    public Guid? OrderId { get; set; }
    public Guid? CoverageRequestId { get; set; }
    public Guid? InstallationId { get; set; }
    public Guid? InvoiceId { get; set; }
    public Guid? PaymentId { get; set; }
    public Guid? DebitOrderMandateId { get; set; }

    public SupportTicketCategory Category { get; set; } = SupportTicketCategory.General;
    public SupportTicketPriority? Priority { get; set; }

    public string Subject { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Application.Billing.Dtos;

public class AdminUpdateDebitOrderMandateStatusDto
{
    public DebitOrderMandateStatus Status { get; set; }
    public string? AdminNotes { get; set; }
}

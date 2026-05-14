using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Application.Billing.Dtos;

public class DebitOrderMandateDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? CustomerProfileId { get; set; }
    public Guid? OrderId { get; set; }

    public DebitOrderMandateStatus Status { get; set; }
    public DebitOrderFrequency Frequency { get; set; }

    public decimal? Amount { get; set; }
    public string CurrencyCode { get; set; } = "ZAR";
    public int? PreferredDebitDay { get; set; }

    public DateTime? StartDateUtc { get; set; }
    public DateTime? EndDateUtc { get; set; }
    public DateTime? ActivatedAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public DateTime? SuspendedAtUtc { get; set; }

    public string? AccountHolderName { get; set; }
    public string? BankName { get; set; }
    public string? BankAccountLast4 { get; set; }
    public string? BankAccountType { get; set; }
    public string? MaskedAccountReference { get; set; }
    public string? GatewayMandateReference { get; set; }
    public string? ExternalReference { get; set; }

    public string? Notes { get; set; }
    public string? AdminNotes { get; set; }

    public Guid? LastStatusChangedByUserId { get; set; }
    public string? LastStatusChangedByUserEmail { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

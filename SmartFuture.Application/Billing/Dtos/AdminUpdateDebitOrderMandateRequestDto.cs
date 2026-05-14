using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Application.Billing.Dtos;

public class AdminUpdateDebitOrderMandateRequestDto
{
    public DebitOrderFrequency Frequency { get; set; } = DebitOrderFrequency.Monthly;
    public decimal? Amount { get; set; }
    public int? PreferredDebitDay { get; set; }

    public DateTime? StartDateUtc { get; set; }
    public DateTime? EndDateUtc { get; set; }

    public string? AccountHolderName { get; set; }
    public string? BankName { get; set; }
    public string? BankAccountLast4 { get; set; }
    public string? BankAccountType { get; set; }
    public string? MaskedAccountReference { get; set; }
    public string? GatewayMandateReference { get; set; }
    public string? ExternalReference { get; set; }

    public string? Notes { get; set; }
    public string? AdminNotes { get; set; }
}

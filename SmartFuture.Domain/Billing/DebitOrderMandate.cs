using SmartFuture.Domain.Common;
using SmartFuture.Domain.Customers;
using SmartFuture.Domain.Identity;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Domain.Billing;

public class DebitOrderMandate : BaseEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid? CustomerProfileId { get; set; }
    public CustomerProfile? CustomerProfile { get; set; }

    public Guid? OrderId { get; set; }
    public Order? Order { get; set; }

    public DebitOrderMandateStatus Status { get; set; } = DebitOrderMandateStatus.Pending;
    public DebitOrderFrequency Frequency { get; set; } = DebitOrderFrequency.Monthly;

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
    public User? LastStatusChangedByUser { get; set; }
}

using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Application.Payments.Recurring.Dtos;

/// <summary>
/// Read-only view of a <c>ServiceBillingSchedule</c>. Identifiers + billing
/// cadence only — no customer contact details.
/// </summary>
public sealed class ServiceBillingScheduleDto
{
    public Guid Id { get; set; }
    public Guid NetworkAccountId { get; set; }
    public Guid OrderId { get; set; }
    public Guid UserId { get; set; }

    public ServicePackageBillingCycle BillingCycle { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "ZAR";

    public int AnchorDayOfMonth { get; set; }
    public DateTime? CurrentPeriodStartUtc { get; set; }
    public DateTime? CurrentPeriodEndUtc { get; set; }
    public DateTime? NextInvoiceDateUtc { get; set; }
    public DateTime? NextDueDateUtc { get; set; }
    public DateTime? LastInvoicedPeriodEndUtc { get; set; }
    public Guid? LastInvoiceId { get; set; }

    public ServiceBillingScheduleStatus Status { get; set; }
    public bool IsAutoBillable { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

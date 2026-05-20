using SmartFuture.Application.NetworkAccounts.Dtos;
using SmartFuture.Shared.Enums.NetworkAccounts;

namespace SmartFuture.Application.Billing.Dtos;

/// <summary>
/// Aggregated customer-facing billing snapshot. Powers the Client Zone
/// /client/billing dashboard so the page renders in a single round-trip
/// instead of stitching three separate list endpoints together. Per-
/// service rows include the same computed billing fields as
/// NetworkAccountDto (see BillingCycleCalculator).
/// </summary>
public class BillingOverviewDto
{
    public decimal OutstandingBalance { get; set; }
    public int InvoiceCount { get; set; }
    public int PaymentCount { get; set; }
    public int DebitOrderCount { get; set; }

    public int ActiveServicesCount { get; set; }
    public int PendingActivationServicesCount { get; set; }

    public decimal ActiveMonthlyTotal { get; set; }
    public decimal PendingMonthlyTotal { get; set; }

    // Earliest upcoming payment across the customer's Active services.
    // Null when no service is currently active.
    public DateTime? NextPaymentDateUtc { get; set; }
    public decimal? NextPaymentAmount { get; set; }
    public Guid? NextPaymentServiceId { get; set; }
    public string? NextPaymentServiceName { get; set; }

    public IReadOnlyList<BillingOverviewServiceDto> Services { get; set; }
        = Array.Empty<BillingOverviewServiceDto>();

    public IReadOnlyList<InvoiceDto> RecentInvoices { get; set; }
        = Array.Empty<InvoiceDto>();

    public IReadOnlyList<PaymentDto> RecentPayments { get; set; }
        = Array.Empty<PaymentDto>();
}

/// <summary>
/// Lean per-service row for the billing overview. Mirrors the fields
/// the billing page needs to show "you have X services, each costs Y,
/// next one is due Z" without re-fetching the full NetworkAccountDto
/// list.
/// </summary>
public class BillingOverviewServiceDto
{
    public Guid ServiceId { get; set; }
    public string ServiceAccountNumber { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public string? PackageSpeedLabel { get; set; }
    public NetworkAccountStatus Status { get; set; }

    public decimal MonthlyPrice { get; set; }
    public DateTime? NextPaymentDateUtc { get; set; }
    public decimal? NextPaymentAmount { get; set; }
    public string? BillingStatusLabel { get; set; }

    public Guid OrderId { get; set; }
    public string? OrderNumber { get; set; }
}

using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Application.Payments.BillingOps.Dtos;

/// <summary>
/// Request to manually create a recurring service invoice when automatic
/// generation failed. Service invoices only — never marks paid, never
/// charges, never calls a provider.
/// </summary>
public sealed class ManualServiceInvoiceRequestDto
{
    public Guid CustomerId { get; set; }
    public Guid NetworkAccountId { get; set; }
    public Guid ServiceBillingScheduleId { get; set; }

    public DateTime PeriodStartUtc { get; set; }
    public DateTime PeriodEndUtc { get; set; }
    public DateTime DueAtUtc { get; set; }

    public decimal Amount { get; set; }

    /// <summary>When true, intentionally create a schedule-detached duplicate (requires reason + force phrase).</summary>
    public bool Force { get; set; }
    public string? ForceReason { get; set; }

    /// <summary>
    /// <c>CREATE_MANUAL_SERVICE_INVOICE</c> for a normal create, or
    /// <c>FORCE_CREATE_DUPLICATE_SERVICE_INVOICE</c> when <see cref="Force"/> is true.
    /// </summary>
    public string ConfirmationPhrase { get; set; } = string.Empty;
}

/// <summary>Existing invoice details surfaced when a duplicate-period create is blocked.</summary>
public sealed class DuplicatePeriodInvoiceDto
{
    public Guid Id { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public InvoiceStatus Status { get; set; }
    public DateTime? PeriodStartUtc { get; set; }
    public DateTime? PeriodEndUtc { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal BalanceDue { get; set; }
}

/// <summary>
/// Result of a manual create. On a duplicate block, <see cref="Code"/> is
/// <c>duplicate_period_invoice</c> and <see cref="ExistingInvoice"/> is
/// populated (the HTTP status is 409). On success the created invoice
/// fields are set.
/// </summary>
public sealed class ManualServiceInvoiceResultDto
{
    /// <summary>Semantic code: <c>created</c>, <c>force_created</c>, or <c>duplicate_period_invoice</c>.</summary>
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    // ─── Success ────────────────────────────────────────────────────
    public Guid? InvoiceId { get; set; }
    public string? InvoiceNumber { get; set; }
    public InvoiceStatus? Status { get; set; }
    public bool ScheduleDetached { get; set; }
    public bool ScheduleAdvanced { get; set; }
    /// <summary>Portal-relative link to the created invoice.</summary>
    public string? InvoiceLink { get; set; }

    // ─── Duplicate block ────────────────────────────────────────────
    public bool CanForceCreate { get; set; }
    public string? ForceRequiredConfirmationPhrase { get; set; }
    public DuplicatePeriodInvoiceDto? ExistingInvoice { get; set; }
}

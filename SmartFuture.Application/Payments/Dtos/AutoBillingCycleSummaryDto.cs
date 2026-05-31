using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.Dtos;

/// <summary>
/// Output of <c>IAutoBillingService.RunAutoBillingCycleAsync</c>.
///
/// The shape is designed to be human-readable in Swagger and to give
/// the admin enough detail to verify the test cycle did what was
/// expected. Per-item rows (<see cref="Items"/>) explain why each
/// invoice was charged / skipped / failed, including any UAT live-
/// override audit notes.
/// </summary>
public class AutoBillingCycleSummaryDto
{
    public Guid RunId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }

    /// <summary>True when the run was a dry-run — no Paystack calls, no DB writes.</summary>
    public bool DryRun { get; set; }

    /// <summary>Email filter applied (null = whole-cycle scan).</summary>
    public string? UserEmailFilter { get; set; }

    /// <summary>Whether the test-amount override is active for this run (UAT only).</summary>
    public bool TestAmountOverrideApplied { get; set; }

    /// <summary>The amount sent to Paystack when override is active.</summary>
    public decimal? OverrideTestAmount { get; set; }

    public int ServicesScanned { get; set; }
    public int ServicesDue { get; set; }
    public int InvoicesCreated { get; set; }
    public int ChargesAttempted { get; set; }
    public int ChargesSucceeded { get; set; }
    public int ChargesFailed { get; set; }
    public int RetriesScheduled { get; set; }
    public int RetriesSkipped { get; set; }
    public int UpToDateCount { get; set; }
    public int ManualPaymentRequiredCount { get; set; }

    public List<string> Errors { get; set; } = new();
    public List<AutoBillingCycleItemDto> Items { get; set; } = new();
}

public class AutoBillingCycleItemDto
{
    public Guid InvoiceId { get; set; }
    public string? InvoiceNumber { get; set; }
    public Guid CustomerId { get; set; }
    public string? CustomerEmail { get; set; }

    /// <summary>Invoice balance at the moment of evaluation.</summary>
    public decimal InvoiceBalanceDue { get; set; }

    /// <summary>What we asked Paystack to charge (= InvoiceBalanceDue normally; differs only when test-override applied).</summary>
    public decimal? AmountChargedToProvider { get; set; }

    /// <summary>One of: Charged, Failed, Skipped, NoMandate, NotOptedIn, AlreadyPaid, DryRunOnly.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Why we landed on <see cref="Outcome"/>.</summary>
    public string? Reason { get; set; }

    public AutoBillingChargeSource? Source { get; set; }

    public int AttemptNumber { get; set; }

    public Guid? PaymentId { get; set; }
    public string? PaymentNumber { get; set; }
    public Guid? PaymentInitiationId { get; set; }
    public Guid? RetryAttemptId { get; set; }
    public string? ProviderReference { get; set; }

    /// <summary>When this attempt failed, the next-attempt UTC the worker should pick up.</summary>
    public DateTime? NextRetryScheduledUtc { get; set; }
}

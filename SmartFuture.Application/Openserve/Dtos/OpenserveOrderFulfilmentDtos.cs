namespace SmartFuture.Application.Openserve.Dtos;

/// <summary>Friendly fulfilment state for Admin Order Detail — derived from the existing domain states, never persisted.</summary>
public static class OpenserveFulfilmentState
{
    public const string NotApplicable = "NotApplicable";
    public const string IntegrationDisabled = "IntegrationDisabled";
    public const string AwaitingPayment = "AwaitingPayment";
    public const string NotSubmitted = "NotSubmitted";
    public const string SubmissionPending = "SubmissionPending";
    public const string Submitted = "Submitted";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public const string FailedRetryable = "FailedRetryable";
    public const string FailedOutcomeUnknown = "FailedOutcomeUnknown";
    public const string FailedRejected = "FailedRejected";
    public const string BlockedConfiguration = "BlockedConfiguration";
    public const string BlockedPackageMapping = "BlockedPackageMapping";
    public const string BlockedOrderData = "BlockedOrderData";
    public const string BlockedAdmin = "BlockedAdmin";
    public const string OrderCancelled = "OrderCancelled";
}

/// <summary>
/// Admin-only "OPENserve Fulfilment" card for one SmartFuture order. Answers:
/// was it forwarded to Openserve, and if not why, can SmartFuture retry by
/// itself, can Admin send/retry now. Never carries credentials, request
/// headers or payloads — those stay in the Integrations → Openserve logs.
/// </summary>
public class OpenserveOrderFulfilmentDto
{
    public Guid OrderId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public string OrderStatus { get; set; } = string.Empty;
    public string InstallationAddress { get; set; } = string.Empty;

    /// <summary>False for non-Fibre orders — the card isn't shown.</summary>
    public bool AppliesToOrder { get; set; }
    public bool IntegrationEnabled { get; set; }

    /// <summary>The headline answer: did Openserve accept this order?</summary>
    public bool ForwardedToOpenserve { get; set; }
    public string State { get; set; } = OpenserveFulfilmentState.NotSubmitted;
    public string StateLabel { get; set; } = string.Empty;
    /// <summary>Why it's in this state — always set when not forwarded.</summary>
    public string? StateReason { get; set; }

    public Guid? OpenserveOrderRecordId { get; set; }
    public string? ExternalReferenceNumber { get; set; }
    public string? OpenserveOrderId { get; set; }
    public string? OpenserveOrderName { get; set; }
    public string? NormalizedStatus { get; set; }
    public string? RawState { get; set; }
    public bool IsTerminal { get; set; }

    /// <summary>Product/SKU/capacity: from the record's mapping once submitted, otherwise the package's current enabled mapping (what a send would use).</summary>
    public string? ProductName { get; set; }
    public string? Sku { get; set; }
    public string? Capacity { get; set; }
    public string? CapacityUom { get; set; }
    public bool HasEnabledMapping { get; set; }

    public string? SubscriberReferenceNumber { get; set; }
    public string? AmId { get; set; }
    public string? BuildingNumId { get; set; }

    public DateTime? LastSubmissionAttemptAtUtc { get; set; }
    public string? LastSubmissionTrigger { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? LastSuccessfulSyncAtUtc { get; set; }
    public DateTime? LastOpenserveUpdateAtUtc { get; set; }

    public int AttemptCount { get; set; }
    public int AutomaticRetryCount { get; set; }
    public int MaxAutomaticRetries { get; set; }

    public string LastFailureClass { get; set; } = "None";
    public string? LastFailureCode { get; set; }
    public string? LastFailureMessage { get; set; }
    public string? FailureClassExplanation { get; set; }
    public DateTime? NextAutomaticRetryAtUtc { get; set; }

    public OpenserveFulfilmentPermissionDto AutomaticRetry { get; set; } = new();
    public OpenserveManualSubmissionDto ManualSubmission { get; set; } = new();
    public OpenserveAutomationPauseStateDto Automation { get; set; } = new();

    public IReadOnlyList<OpenserveFulfilmentActivityDto> Activity { get; set; } = Array.Empty<OpenserveFulfilmentActivityDto>();
}

public class OpenserveFulfilmentPermissionDto
{
    public bool Allowed { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class OpenserveManualSubmissionDto
{
    public bool Allowed { get; set; }
    /// <summary>"Send" (no attempt yet) | "Retry" (an attempt exists) | null when nothing can be done.</summary>
    public string? Action { get; set; }
    public string Reason { get; set; } = string.Empty;
    /// <summary>The last attempt's outcome is unknown — Admin must confirm with Openserve before resending.</summary>
    public bool RequiresOutcomeConfirmation { get; set; }
}

public class OpenserveAutomationPauseStateDto
{
    public bool Paused { get; set; }
    public DateTime? PausedAtUtc { get; set; }
    public string? PausedBy { get; set; }
    public string? Reason { get; set; }
    public bool CanPause { get; set; }
    public bool CanResume { get; set; }
    public string? CannotPauseReason { get; set; }
}

public class OpenserveFulfilmentActivityDto
{
    public DateTime OccurredAtUtc { get; set; }
    /// <summary>Submission | Status | Sync | Cancellation | Automation</summary>
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Detail { get; set; }
    /// <summary>success | failure | warning | info</summary>
    public string Tone { get; set; } = "info";
}

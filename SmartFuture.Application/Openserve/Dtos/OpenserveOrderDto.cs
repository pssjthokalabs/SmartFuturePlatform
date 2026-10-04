using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Application.Openserve.Dtos;

public class OpenserveOrderDto
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string? OrderNumber { get; set; }
    public string? CustomerName { get; set; }
    public string? PackageName { get; set; }

    public string ExternalReferenceNumber { get; set; } = string.Empty;
    public string? SubscriberReferenceNumber { get; set; }
    public string? OpenserveOrderId { get; set; }
    public string? OpenserveOrderName { get; set; }

    public string OrderType { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? RawState { get; set; }
    /// <summary>Serialized as a name (e.g. "InProgress"), not a number — no JsonStringEnumConverter is registered on the API, so a raw enum property here would ship over JSON as a bare integer.</summary>
    public string NormalizedStatus { get; set; } = string.Empty;
    public bool IsTerminal { get; set; }

    public string? Sku { get; set; }
    public string? Capacity { get; set; }
    public string? CapacityUom { get; set; }

    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? LastOpenserveUpdateAtUtc { get; set; }
    public DateTime? LastSuccessfulSyncAtUtc { get; set; }

    public int RetryCount { get; set; }
    public string? LastFailureCode { get; set; }
    public string? LastFailureMessage { get; set; }

    /// <summary>OpenserveSubmissionFailureClass name: None | Retryable | NonRetryable | Blocked | OutcomeUnknown.</summary>
    public string LastFailureClass { get; set; } = nameof(OpenserveSubmissionFailureClass.None);
    public DateTime? LastSubmissionAttemptAtUtc { get; set; }
    public string? LastSubmissionTrigger { get; set; }
    public int AutomaticRetryCount { get; set; }
    public DateTime? NextAutomaticRetryAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

// ─── Submission coordinator ─────────────────────────────────────────

/// <summary>One request to the submission coordinator. Every path (automatic trigger, Admin, recovery worker, safety sweep) builds one of these.</summary>
public sealed record OpenserveSubmissionRequest(Guid OrderId, OpenserveSubmissionTrigger Trigger)
{
    /// <summary>The automatic trigger passes the account it just reserved; other paths use the order's non-terminated account.</summary>
    public Guid? NetworkAccountId { get; init; }

    /// <summary>Admin has confirmed with Openserve that a previous attempt whose outcome is unknown did NOT create an order.</summary>
    public bool ConfirmOutcomeUnknown { get; init; }
}

public static class OpenserveSubmissionOutcome
{
    /// <summary>Openserve accepted the order.</summary>
    public const string Submitted = "Submitted";

    /// <summary>The request went to Openserve and failed — see FailureClass.</summary>
    public const string Failed = "Failed";

    /// <summary>Never sent: a SmartFuture-side precondition failed.</summary>
    public const string Blocked = "Blocked";
}

public class OpenserveSubmissionAttemptDto
{
    public Guid OrderId { get; set; }
    public Guid OpenserveOrderRecordId { get; set; }
    public string Outcome { get; set; } = OpenserveSubmissionOutcome.Blocked;
    /// <summary>True when an HTTP request actually left SmartFuture on this attempt.</summary>
    public bool RequestSent { get; set; }
    public string FailureClass { get; set; } = nameof(OpenserveSubmissionFailureClass.None);
    public string Message { get; set; } = string.Empty;
}

public class SubmitOpenserveOrderRequestDto
{
    /// <summary>Required (true) to retry an attempt whose outcome is unknown — Admin confirms Openserve has no order for this reference.</summary>
    public bool ConfirmOutcomeUnknown { get; set; }
}

public class OpenserveAutomationPauseRequestDto
{
    public string? Reason { get; set; }
}

public class OpenserveOrderStatusHistoryDto
{
    public Guid Id { get; set; }
    public Guid OpenserveOrderId { get; set; }
    public Guid OrderId { get; set; }
    public string? OpenserveEventId { get; set; }
    public string? CorrelationId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? PreviousRawState { get; set; }
    public string? NewRawState { get; set; }
    public string NormalizedStatus { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? InstallationStatus { get; set; }
    public DateTime? AppointmentAtUtc { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime? EventOccurredAtUtc { get; set; }
    public Guid? IntegrationLogId { get; set; }
    public string? ProcessingResult { get; set; }
    public bool NotificationTriggered { get; set; }
}

public class OpenserveIntegrationLogDto
{
    public Guid Id { get; set; }
    public Guid? OpenserveOrderId { get; set; }
    public string? OpenserveOrderExternalReferenceNumber { get; set; }
    public string Direction { get; set; } = string.Empty;
    public string OperationType { get; set; } = string.Empty;
    public string? MessageId { get; set; }
    public string? CorrelationId { get; set; }
    public string? HttpMethod { get; set; }
    public string? Endpoint { get; set; }
    /// <summary>Redacted before persisting (see OpenserveIntegrationLog remarks) — safe to display verbatim.</summary>
    public string? RequestHeadersJson { get; set; }
    public string? RequestBodyJson { get; set; }
    public int? ResponseStatusCode { get; set; }
    public string? ResponseBodyJson { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public bool IsSuccess { get; set; }
    public string? ErrorSummary { get; set; }
}

using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Application.Openserve.Dtos;

// ─── Integration Logs (global, admin console "Logs" tab) ────────────

public class OpenserveIntegrationLogFilterRequestDto
{
    public OpenserveIntegrationDirection? Direction { get; set; }
    public OpenserveOperationType? OperationType { get; set; }
    public bool? IsSuccess { get; set; }
    public Guid? OpenserveOrderId { get; set; }
    /// <summary>Matches against ExternalReferenceNumber, OpenserveOrderId (Openserve's own id string via the related order), Endpoint, or MessageId.</summary>
    public string? Search { get; set; }
    public DateTime? DateFromUtc { get; set; }
    public DateTime? DateToUtc { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

// ─── Configuration ──────────────────────────────────────────────────

public class OpenserveConfigurationDto
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public bool ApiKeyConfigured { get; set; }
    public string? ApiKeyMasked { get; set; }
    public string WsIspCode { get; set; } = string.Empty;
    public string IspIdentifier { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    public string ReplyToAddress { get; set; } = string.Empty;
    public string EventNotificationUrl { get; set; } = string.Empty;
    public int HttpTimeoutSeconds { get; set; }
    public int PollingFallbackIntervalMinutes { get; set; }
    /// <summary>Serialized as a name ("None" | "SharedSecretHeader" | "IpAllowlist") — see the NormalizedStatus remark on OpenserveOrderDto for why raw enums aren't used on API DTOs here.</summary>
    public string CallbackAuthMode { get; set; } = "None";
    public bool SharedSecretConfigured { get; set; }
    public string? SharedSecretMasked { get; set; }
    public string[] AllowedIpRanges { get; set; } = Array.Empty<string>();

    /// <summary>Informational only — see brief §2: Retry.MaxAttempts/BaseDelaySeconds exist in appsettings but are not actually wired into any automatic transport retry. Not editable here.</summary>
    public int RetryMaxAttempts { get; set; }
    public int RetryBaseDelaySeconds { get; set; }
    public bool RetryIsWired { get; set; }

    public DateTime? LastUpdatedAtUtc { get; set; }
    public string? LastUpdatedByUserEmail { get; set; }

    /// <summary>Per-field provenance: "Database" (admin override) or "AppSettings" (env var/appsettings.json fallback) — so the admin never has to guess where a value actually came from.</summary>
    public Dictionary<string, string> FieldSources { get; set; } = new();
}

public class UpdateOpenserveConfigurationRequestDto
{
    public bool? Enabled { get; set; }
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public bool ClearApiKey { get; set; }
    public string? WsIspCode { get; set; }
    public string? IspIdentifier { get; set; }
    public string? SenderId { get; set; }
    public string? ReplyToAddress { get; set; }
    public string? EventNotificationUrl { get; set; }
    public int? HttpTimeoutSeconds { get; set; }
    public int? PollingFallbackIntervalMinutes { get; set; }
    /// <summary>"None" | "SharedSecretHeader" | "IpAllowlist" — parsed server-side; invalid/omitted values leave the stored mode unchanged.</summary>
    public string? CallbackAuthMode { get; set; }
    public string? SharedSecret { get; set; }
    public bool ClearSharedSecret { get; set; }
    public string[]? AllowedIpRanges { get; set; }
}

// ─── Overview / health ──────────────────────────────────────────────

public class OpenserveIntegrationOverviewDto
{
    public string OverallState { get; set; } = "NotReady"; // NotReady | ReadyForUat | Healthy | Degraded | Disabled
    public string OverallStateLabel { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool ConfigurationComplete { get; set; }
    public List<string> MissingConfiguration { get; set; } = new();

    public string? BaseUrl { get; set; }
    public string EnvironmentLabel { get; set; } = "Unknown"; // Test | Production | Unknown
    public bool EnvironmentMismatchWarning { get; set; }
    public string? EnvironmentMismatchMessage { get; set; }

    public string? WsIspCode { get; set; }
    public string? IspIdentifier { get; set; }
    public string? SenderId { get; set; }
    public bool ApiKeyConfigured { get; set; }
    public string? ReplyToAddress { get; set; }
    public string? EventNotificationUrl { get; set; }
    public string CallbackAuthMode { get; set; } = "None";

    public bool ReconciliationRunning { get; set; }
    public int PollingIntervalMinutes { get; set; }

    public DateTime? LastSuccessfulApiCallAtUtc { get; set; }
    public string? LastSuccessfulApiCallOperation { get; set; }
    public DateTime? LastFailedApiCallAtUtc { get; set; }
    public string? LastFailedApiCallOperation { get; set; }
    public string? LastFailedApiCallError { get; set; }

    public DateTime? LastCallbackReceivedAtUtc { get; set; }
    public DateTime? LastEventNotificationReceivedAtUtc { get; set; }

    public int PendingOrders { get; set; }
    public int FailedSubmissions { get; set; }
    public int NonTerminalOrders { get; set; }
    public int CompletedOrders { get; set; }
    public int CancelledOrders { get; set; }
    public int TotalOrders { get; set; }
    public int UnmappedActiveFibrePackages { get; set; }
}

// ─── Readiness check ────────────────────────────────────────────────

public class OpenserveReadinessCheckDto
{
    public bool IsReady { get; set; }
    public int PassedCount { get; set; }
    public int TotalCount { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<OpenserveReadinessCheckItemDto> Checks { get; set; } = new();
}

public class OpenserveReadinessCheckItemDto
{
    public string Name { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string? Detail { get; set; }
}

// ─── Configuration check / test connection ──────────────────────────

public class OpenserveConfigurationCheckResultDto
{
    public bool Valid { get; set; }
    public List<string> Issues { get; set; } = new();
}

public class OpenserveTestConnectionResultDto
{
    public bool Success { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? OpenserveResultCode { get; set; }
    public string? Message { get; set; }
    public double DurationMs { get; set; }
    public DateTime TimestampUtc { get; set; }
}

// ─── Product Qualification diagnostic tool ──────────────────────────

public class RunOpenserveQualificationTestRequestDto
{
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string? Amid { get; set; }
    public bool BuildingInfo { get; set; } = true;
}

public class OpenserveQualificationTestResultDto
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public double DurationMs { get; set; }
    public DateTime TimestampUtc { get; set; }

    public string? Amid { get; set; }
    public string? MatchedAddress { get; set; }
    public string? Suburb { get; set; }
    public string? Town { get; set; }
    public string? Province { get; set; }
    public string? FtthStatus { get; set; }
    public decimal? FibreMaxSpeed { get; set; }
    public string? FibreMaxSpeedUnit { get; set; }
    public string? BuildingNumId { get; set; }
    public int BuildingMatchCount { get; set; }
    public List<OpenserveQualificationTestProductDto> AvailableProducts { get; set; } = new();

    public string? RawResponseJson { get; set; }
}

public class OpenserveQualificationTestProductDto
{
    public string? ProductName { get; set; }
    public string? ProductCode { get; set; }
    public string? UpstreamSpeed { get; set; }
    public string? DownstreamSpeed { get; set; }
}

// ─── GET Product Order diagnostic tool ──────────────────────────────

public class OpenserveOrderLookupTestResultDto
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public double DurationMs { get; set; }
    public DateTime TimestampUtc { get; set; }

    public string? OpenserveOrderId { get; set; }
    public string? OpenserveOrderName { get; set; }
    public string? ServiceOrderNumber { get; set; }
    public string? RawState { get; set; }
    public string? OrderDate { get; set; }

    // Populated only when we ALSO hold a matching OpenserveOrder row —
    // cross-references Openserve's live answer against our own record.
    public bool KnownLocally { get; set; }
    public Guid? LocalOpenserveOrderId { get; set; }
    public Guid? LocalOrderId { get; set; }
    public string? LocalOrderNumber { get; set; }
    public string? ExternalReferenceNumber { get; set; }
    public string? SubscriberReferenceNumber { get; set; }
    public string? NormalizedStatus { get; set; }
    public bool? IsTerminal { get; set; }

    public string? RawResponseJson { get; set; }
}

// ─── Callback / event health ────────────────────────────────────────

public class OpenserveCallbackHealthDto
{
    public string CallbackUrl { get; set; } = string.Empty;
    public string EventUrl { get; set; } = string.Empty;
    public string CallbackAuthMode { get; set; } = "None";

    public DateTime? LastCallbackReceivedAtUtc { get; set; }
    public DateTime? LastEventReceivedAtUtc { get; set; }
    public string? LastEventType { get; set; }
    public string? LastEventId { get; set; }
    public string? LastProcessingResult { get; set; }

    public int SuccessfulInboundCount { get; set; }
    public int FailedInboundCount { get; set; }

    public List<OpenserveRecentEventDto> RecentEvents { get; set; } = new();
}

public class OpenserveRecentEventDto
{
    public Guid LogId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string Type { get; set; } = string.Empty; // "Callback" | "Event"
    public string? EventId { get; set; }
    public string? OpenserveOrderId { get; set; }
    public string? RawState { get; set; }
    public string Result { get; set; } = string.Empty; // Success | Failed
}

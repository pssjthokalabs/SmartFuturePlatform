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
    public const string BlockedBuildingUnit = "BlockedBuildingUnit";
    public const string BlockedQualification = "BlockedQualification";
    public const string BlockedAddressUnresolved = "BlockedAddressUnresolved";
    public const string BlockedAddressReview = "BlockedAddressReview";
    public const string BlockedFibreUnavailable = "BlockedFibreUnavailable";
    public const string BlockedProductUnavailable = "BlockedProductUnavailable";
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
    public OpenserveQualificationStateDto Qualification { get; set; } = new();

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

/// <summary>Openserve Product Qualification for this order — what Admin needs to see before a submission can go.</summary>
public class OpenserveQualificationStateDto
{
    /// <summary>
    /// NotRun | Failed | EvidenceMissing (AMID from before evidence was recorded) | AddressUnresolved (nearby Openserve
    /// records, none established as the customer's premises) | NoAddressCandidates | AddressReviewRequired |
    /// FtthUnavailable (the customer's established premises has no Fibre) | ProductUnavailable | BuildingUnitRequired |
    /// Orderable. These never collapse into one "no coverage": only FtthUnavailable says Fibre is unavailable.
    /// </summary>
    public string Status { get; set; } = "NotRun";

    /// <summary>Plain-language headline for <see cref="Status"/>, e.g. "Address identified — NOT orderable".</summary>
    public string StatusLabel { get; set; } = "Not run";
    public DateTime? QualifiedAtUtc { get; set; }

    /// <summary>The evidence row the order is assessed on (OpenserveQualificationResults).</summary>
    public Guid? EvidenceId { get; set; }

    // ── ADDRESS VERIFICATION (FORCEVERIFY → AddressVerify[]) ──
    /// <summary>NotEvaluated (legacy nearest-address lookup) | AutoMatched | AdminSelected | Unresolved | NoCandidates</summary>
    public string AddressResolution { get; set; } = "NotEvaluated";
    public string AddressResolutionLabel { get; set; } = "Not verified";
    public string? AddressResolutionDetail { get; set; }
    public DateTime? AddressVerifiedAtUtc { get; set; }

    /// <summary>The customer's coordinates Openserve was asked about.</summary>
    public decimal? CustomerLatitude { get; set; }
    public decimal? CustomerLongitude { get; set; }

    /// <summary>Nearby Openserve Address Master records exactly as AddressVerify returned them, each with SmartFuture's match verdict.</summary>
    public IReadOnlyList<OpenserveAddressCandidateDto> AddressCandidates { get; set; } = Array.Empty<OpenserveAddressCandidateDto>();

    /// <summary>Admin may explicitly choose one of <see cref="AddressCandidates"/> as the customer's premises.</summary>
    public bool CanSelectAddressCandidate { get; set; }
    public string? CannotSelectAddressCandidateReason { get; set; }

    /// <summary>Set when an Admin chose the premises.</summary>
    public string? AddressResolvedBy { get; set; }
    public DateTime? AddressResolvedAtUtc { get; set; }
    public string? AddressResolutionNote { get; set; }

    /// <summary>Which path produced the evidence (CheckoutGate, PaymentConversion, AdminManual, …).</summary>
    public string? EvidenceSource { get; set; }

    // ── ADDRESS ──
    public bool AddressIdentified { get; set; }

    /// <summary>Openserve's canonical address for the AMID (LR_Address), e.g. "8 PALMAS ST MONAVONI X 6 CENTURION".</summary>
    public string? OpenserveAddress { get; set; }
    public string? CustomerAddress { get; set; }
    public decimal? DistanceMeters { get; set; }
    public string? DistanceText { get; set; }
    public string? Region { get; set; }
    public string? AddressStatus { get; set; }
    public string? MduVerification { get; set; }

    /// <summary>NotEvaluated | Matched | ReviewRequired | Mismatch</summary>
    public string AddressMatch { get; set; } = "NotEvaluated";
    public string? AddressMatchDetail { get; set; }
    public bool AddressAccepted { get; set; }
    public string? AddressAcceptedBy { get; set; }
    public DateTime? AddressAcceptedAtUtc { get; set; }
    public string? AddressAcceptanceNote { get; set; }

    /// <summary>Admin may confirm that Openserve's address is the customer's property.</summary>
    public bool CanAcceptAddress { get; set; }
    public string? CannotAcceptAddressReason { get; set; }

    // ── FIBRE ──
    /// <summary>NotEvaluated | Available | NotYetAvailable | NotReturned</summary>
    public string FibreAvailability { get; set; } = "NotEvaluated";
    public string FibreAvailabilityLabel { get; set; } = "Not evaluated";
    public string? FtthStatus { get; set; }
    public decimal? FibreMaxSpeedMbps { get; set; }
    public IReadOnlyList<OpenserveQualificationInfrastructureDto> Infrastructures { get; set; } = Array.Empty<OpenserveQualificationInfrastructureDto>();
    public string? EthernetProductCodes { get; set; }
    public string? FwaStatus { get; set; }

    // ── PRODUCT ──
    /// <summary>The package's current mapping, e.g. "OFC 50 Mbps".</summary>
    public string? MappedProduct { get; set; }

    /// <summary>NotEvaluated | Eligible | FibreUnavailable | ProductUnavailable | CapabilityUnavailable | NoMapping</summary>
    public string ProductEligibility { get; set; } = "NotEvaluated";
    public string? ProductEligibilityReason { get; set; }

    /// <summary>Fibre available + mapped product/capacity offered + address confirmed (building/unit is separate — see <see cref="Orderable"/>).</summary>
    public bool Eligible { get; set; }

    /// <summary>Everything satisfied, including the MDU building/unit.</summary>
    public bool Orderable { get; set; }

    /// <summary>Why submission is blocked by qualification, in Admin language (null when eligible / not evaluated).</summary>
    public string? EligibilityBlocker { get; set; }
    public string? AmId { get; set; }
    public string? BuildingNumId { get; set; }
    public string? BuildingName { get; set; }
    public string? Floor { get; set; }
    public string? Unit { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>NotApplicable (no AMID yet) | Resolved | NotRequired (no building rows) | NeedsResolution (several candidates, none chosen — blocks submission, never guessed)</summary>
    public string BuildingResolution { get; set; } = "NotApplicable";
    public string? BuildingNote { get; set; }

    public bool CoordinatesAvailable { get; set; }
    public string? PropertyType { get; set; }
    public string? BuildingComplexName { get; set; }
    public string? UnitNumber { get; set; }

    /// <summary>Admin may press "Run Product Qualification" (or "Re-run" once an AMID exists — never on an order already with Openserve).</summary>
    public bool CanRun { get; set; }
    public string RunLabel { get; set; } = "Run Product Qualification";
    public string? CannotRunReason { get; set; }

    /// <summary>buildingInfo rows Openserve returned for the AMID (null = not recorded).</summary>
    public int? BuildingCandidateCount { get; set; }
    public IReadOnlyList<OpenserveBuildingCandidateDto> BuildingCandidates { get; set; } = Array.Empty<OpenserveBuildingCandidateDto>();

    /// <summary>Admin may pick the building/unit from <see cref="BuildingCandidates"/>.</summary>
    public bool CanSelectBuilding { get; set; }
    public string? CannotSelectBuildingReason { get; set; }

    /// <summary>Admin may reload the building/unit rows for the existing AMID (e.g. qualified before rows were stored).</summary>
    public bool CanRefreshBuildingCandidates { get; set; }
    public string? CannotRefreshBuildingCandidatesReason { get; set; }

    /// <summary>Set when the current building/unit was chosen by an Admin.</summary>
    public string? BuildingSelectedBy { get; set; }
    public DateTime? BuildingSelectedAtUtc { get; set; }
}

/// <summary>One AddressVerify[] candidate (an Openserve Address Master record near the customer's location).</summary>
public class OpenserveAddressCandidateDto
{
    public string? Amid { get; set; }
    public string? Address { get; set; }
    public decimal? DistanceMeters { get; set; }
    public string? DistanceText { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    /// <summary>Matched | StreetNumberMismatch | StreetMismatch | LocalityMismatch | NotComparable</summary>
    public string Match { get; set; } = string.Empty;
    public string MatchLabel { get; set; } = string.Empty;
    public string? MatchDetail { get; set; }

    /// <summary>This AMID is the order's current premises.</summary>
    public bool IsSelected { get; set; }
}

public class SelectOpenserveAddressCandidateRequestDto
{
    /// <summary>Must be the AMID of one of the AddressVerify candidates recorded for the order.</summary>
    public string? Amid { get; set; }

    /// <summary>Required: how the Admin confirmed this Openserve record is the customer's premises.</summary>
    public string? Note { get; set; }
}

/// <summary>One FTTH infrastructure entry from Product Qualification with the products offered on it.</summary>
public class OpenserveQualificationInfrastructureDto
{
    public int Index { get; set; }

    /// <summary>"Openserve network" for the own-network entry, else FTTH_Type (e.g. "3rd_Party").</summary>
    public string Network { get; set; } = string.Empty;
    public string? FtthStatus { get; set; }
    public bool ImmediatelyAvailable { get; set; }
    public decimal? MaxSpeedMbps { get; set; }
    public string? ServiceProviderId { get; set; }
    public IReadOnlyList<OpenserveQualificationProductDto> Products { get; set; } = Array.Empty<OpenserveQualificationProductDto>();
}

public class OpenserveQualificationProductDto
{
    public string? ProductCode { get; set; }
    public string? ProductName { get; set; }
    public string? UpstreamSpeed { get; set; }
    public string? DownstreamSpeed { get; set; }

    /// <summary>This ProductCode is the package's mapped SKU.</summary>
    public bool IsMappedProduct { get; set; }
}

public class AcceptOpenserveAddressRequestDto
{
    /// <summary>Required: how the Admin confirmed Openserve's address is the customer's property.</summary>
    public string? Note { get; set; }
}

/// <summary>One buildingInfo row exactly as Openserve returned it.</summary>
public class OpenserveBuildingCandidateDto
{
    public string? BldNumId { get; set; }
    public string? BldId { get; set; }
    public string? FloorId { get; set; }
    public string? Num { get; set; }
    public string? BuildingName { get; set; }
    public string? Floor { get; set; }
    /// <summary>This is the order's current OpenserveBuildingNumId.</summary>
    public bool IsSelected { get; set; }
    /// <summary>NUM equals the customer's own unit number (normalised) — a hint, not a choice.</summary>
    public bool MatchesCustomerUnit { get; set; }
}

public class SelectOpenserveBuildingUnitRequestDto
{
    /// <summary>Must be the BLD_NUM_ID of one of the rows Openserve returned for this order.</summary>
    public string? BldNumId { get; set; }
}

public class OpenserveFulfilmentActivityDto
{
    public DateTime OccurredAtUtc { get; set; }
    /// <summary>Submission | Status | Sync | Cancellation | Automation | Qualification</summary>
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Detail { get; set; }
    /// <summary>success | failure | warning | info</summary>
    public string Tone { get; set; } = "info";
}

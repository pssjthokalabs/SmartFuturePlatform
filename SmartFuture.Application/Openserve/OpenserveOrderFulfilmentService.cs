using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Openserve;

public interface IOpenserveOrderFulfilmentService
{
    Task<Result<OpenserveOrderFulfilmentDto>> GetAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>Admin Send / Retry from Order Detail — the same coordinator as every other path.</summary>
    Task<Result<OpenserveOrderFulfilmentDto>> SubmitAsync(Guid orderId, bool confirmOutcomeUnknown, CancellationToken cancellationToken = default);

    Task<Result<OpenserveOrderFulfilmentDto>> PauseAutomationAsync(Guid orderId, string? reason, CancellationToken cancellationToken = default);

    Task<Result<OpenserveOrderFulfilmentDto>> ResumeAutomationAsync(Guid orderId, string? reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Admin "Run Product Qualification" for a Fibre order with no AMID. Runs the
    /// shared qualification routine and stores the result — it never sends the
    /// order to Openserve; Admin reviews the result, then Sends/Retries.
    /// </summary>
    Task<Result<OpenserveOrderFulfilmentDto>> RunQualificationAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Admin picks the order's building/unit from the rows Openserve returned.
    /// Only a returned BLD_NUM_ID is accepted; the choice is audited. Never
    /// submits — Admin then Sends/Retries.
    /// </summary>
    Task<Result<OpenserveOrderFulfilmentDto>> SelectBuildingUnitAsync(Guid orderId, string? bldNumId, CancellationToken cancellationToken = default);

    /// <summary>Reloads the building/unit rows for the order's existing AMID. Never changes the AMID, never submits.</summary>
    Task<Result<OpenserveOrderFulfilmentDto>> RefreshBuildingCandidatesAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Admin confirms that the address Openserve resolved for the order's AMID
    /// IS the customer's property (address review / mismatch), with a required
    /// note. Audited, recorded on the evidence row only. Never submits, and
    /// never overrides Fibre/product availability.
    /// </summary>
    Task<Result<OpenserveOrderFulfilmentDto>> AcceptAddressAsync(Guid orderId, string? note, CancellationToken cancellationToken = default);

    /// <summary>
    /// Admin explicitly chooses one of the Openserve AddressVerify candidates as
    /// the customer's premises (note required; audited), then that AMID is
    /// qualified by AMID and eligibility recalculated. Only a candidate
    /// Openserve returned is accepted. Never submits.
    /// </summary>
    Task<Result<OpenserveOrderFulfilmentDto>> SelectAddressCandidateAsync(Guid orderId, string? amid, string? note, CancellationToken cancellationToken = default);
}

/// <summary>
/// Admin Order Detail → "OPENserve Fulfilment". Read model + the per-order
/// Pause/Resume control. Eligibility shown here is computed with the same
/// <see cref="OpenserveSubmissionRules"/> the coordinator enforces, and every
/// action is re-validated server-side, so the UI never offers what the API
/// would refuse. Admin-only — nothing here is used by customer endpoints.
/// </summary>
public class OpenserveOrderFulfilmentService : IOpenserveOrderFulfilmentService
{
    private const int MaxActivityItems = 60;
    private const int MaxPauseReasonLength = 500;
    private const int MinAddressAcceptanceNoteLength = 10;

    private readonly IAppDbContext _dbContext;
    private readonly IOpenserveOrderSubmissionService _submission;
    private readonly IOpenserveRuntimeConfigProvider _configProvider;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly IOpenserveQualificationService _qualification;
    private readonly ILogger<OpenserveOrderFulfilmentService> _logger;

    public OpenserveOrderFulfilmentService(IAppDbContext dbContext, IOpenserveOrderSubmissionService submission, IOpenserveRuntimeConfigProvider configProvider, IAuditService auditService,
        ICurrentUserService currentUser, IOpenserveQualificationService qualification, ILogger<OpenserveOrderFulfilmentService> logger)
    {
        _dbContext = dbContext;
        _submission = submission;
        _configProvider = configProvider;
        _auditService = auditService;
        _currentUser = currentUser;
        _qualification = qualification;
        _logger = logger;
    }

    public async Task<Result<OpenserveOrderFulfilmentDto>> GetAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        try
        {
            var dto = await BuildAsync(orderId, cancellationToken);
            return dto is null
                ? Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.")
                : Result<OpenserveOrderFulfilmentDto>.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error loading Openserve fulfilment for order {OrderId}.", orderId);
            return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.EXCEPTION, "Could not load the Openserve fulfilment state.");
        }
    }

    public async Task<Result<OpenserveOrderFulfilmentDto>> SubmitAsync(Guid orderId, bool confirmOutcomeUnknown, CancellationToken cancellationToken = default)
    {
        var attempt = await _submission.SubmitAsync(new OpenserveSubmissionRequest(orderId, OpenserveSubmissionTrigger.AdminManual) { ConfirmOutcomeUnknown = confirmOutcomeUnknown }, cancellationToken);
        if (!attempt.IsSuccess) return Result<OpenserveOrderFulfilmentDto>.Failure(attempt.Code ?? ErrorCodes.VALIDATION_ERROR, attempt.Message);

        var dto = await BuildAsync(orderId, cancellationToken);
        return dto is null
            ? Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.")
            : Result<OpenserveOrderFulfilmentDto>.Success(dto, attempt.Message);
    }

    public async Task<Result<OpenserveOrderFulfilmentDto>> PauseAutomationAsync(Guid orderId, string? reason, CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.Orders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");
        if (order.PackageType != ServicePackageType.Fibre) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Openserve automation only applies to Fibre orders.");
        if (order.OpenserveAutomationPaused) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.CONFLICT, "Openserve automation is already paused for this order.");

        var record = await FindSalesOrderAsync(orderId, cancellationToken);
        if (record is not null && OpenserveSubmissionRules.IsForwarded(record))
            return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "This order is already with Openserve — there is no submission left to pause. Status tracking continues.");

        var trimmed = Clean(reason);
        order.OpenserveAutomationPaused = true;
        order.OpenserveAutomationPausedAtUtc = DateTime.UtcNow;
        order.OpenserveAutomationPausedByUserId = _currentUser.UserId;
        order.OpenserveAutomationPauseReason = trimmed;
        order.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await AuditAsync(AuditActionType.OpenserveAutomationPaused, order, $"Openserve automation paused for order {order.OrderNumber}{(trimmed is null ? "." : $": {trimmed}")}", trimmed);
        return await GetAsync(orderId, cancellationToken);
    }

    public async Task<Result<OpenserveOrderFulfilmentDto>> ResumeAutomationAsync(Guid orderId, string? reason, CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.Orders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");
        if (!order.OpenserveAutomationPaused) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.CONFLICT, "Openserve automation is not paused for this order.");

        var trimmed = Clean(reason);
        order.OpenserveAutomationPaused = false;
        order.OpenserveAutomationPausedAtUtc = null;
        order.OpenserveAutomationPausedByUserId = null;
        order.OpenserveAutomationPauseReason = null;
        order.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await AuditAsync(AuditActionType.OpenserveAutomationResumed, order, $"Openserve automation resumed for order {order.OrderNumber}{(trimmed is null ? "." : $": {trimmed}")}", trimmed);
        return await GetAsync(orderId, cancellationToken);
    }

    public async Task<Result<OpenserveOrderFulfilmentDto>> RunQualificationAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var before = await BuildAsync(orderId, cancellationToken);
        if (before is null) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");
        if (!before.AppliesToOrder) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Product Qualification only applies to Fibre orders.");
        if (!before.Qualification.CanRun)
            return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.VALIDATION_ERROR, before.Qualification.CannotRunReason ?? "Product Qualification can't run for this order.");

        // Admin asked explicitly — ignore the automatic cooldown. Qualification
        // only: nothing is sent to Openserve's ordering API from here.
        var run = await _qualification.QualifyAndPersistAsync(orderId, OpenserveQualificationTrigger.AdminManual, ignoreCooldown: true, cancellationToken);

        var after = await BuildAsync(orderId, cancellationToken);
        if (after is null) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");
        var message = run.Status switch
        {
            // Unknown is not unavailable: no premises → Fibre at the customer's address isn't known yet.
            OpenserveQualificationRunStatus.AddressUnresolved =>
                $"Address verification complete: {after.Qualification.AddressResolutionDetail ?? run.Message} Fibre availability at the customer's address is not yet known — "
                + "choose the correct Openserve address after confirming with the customer, or request a coverage check. Nothing was sent.",
            // An AMID only identifies the address — say whether the order is actually orderable.
            OpenserveQualificationRunStatus.Qualified => run.FibreEligible == true
                ? $"Product Qualification complete — AMID {run.AmId}; Fibre and the mapped product are available. Nothing was sent — use Send/Retry when ready."
                : $"Product Qualification complete — AMID {run.AmId} (address identified), but the order is NOT orderable: {after.Qualification.EligibilityBlocker ?? run.Message}",
            OpenserveQualificationRunStatus.NoCoordinates => run.Message,
            _ => $"Product Qualification did not return an AMID: {run.Message}"
        };
        return Result<OpenserveOrderFulfilmentDto>.Success(after, message);
    }

    public async Task<Result<OpenserveOrderFulfilmentDto>> SelectAddressCandidateAsync(Guid orderId, string? amid, string? note, CancellationToken cancellationToken = default)
    {
        var before = await BuildAsync(orderId, cancellationToken);
        if (before is null) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");
        if (!before.AppliesToOrder) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Openserve address verification only applies to Fibre orders.");
        if (!before.Qualification.CanSelectAddressCandidate)
            return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.VALIDATION_ERROR, before.Qualification.CannotSelectAddressCandidateReason ?? "The Openserve premises can't be chosen for this order.");

        var trimmed = Clean(note);
        if (trimmed is null || trimmed.Length < MinAddressAcceptanceNoteLength)
            return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.VALIDATION_ERROR,
                $"Add a note (at least {MinAddressAcceptanceNoteLength} characters) saying how you confirmed that this Openserve record is the customer's premises.");
        if (string.IsNullOrWhiteSpace(amid) || before.Qualification.AddressCandidates.All(c => !string.Equals(c.Amid, amid.Trim(), StringComparison.Ordinal)))
            return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "That AMID is not one of the Openserve address records returned for this order's location.");

        // Qualification only — nothing is sent to Openserve's ordering API from here.
        var run = await _qualification.SelectAddressCandidateAsync(orderId, amid.Trim(), trimmed, cancellationToken);
        if (run.Status is OpenserveQualificationRunStatus.Skipped or OpenserveQualificationRunStatus.NotFound)
            return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.VALIDATION_ERROR, run.Message);

        var after = await BuildAsync(orderId, cancellationToken);
        var q = after!.Qualification;
        var message = run.Status != OpenserveQualificationRunStatus.Qualified
            ? $"The chosen Openserve address was recorded, but Product Qualification of AMID {amid.Trim()} did not complete: {run.Message}"
            : q.Orderable
                ? $"Openserve premises set to {q.OpenserveAddress} (AMID {q.AmId}) — Fibre and the mapped product are available. Nothing was sent — use Send/Retry when ready."
                : $"Openserve premises set to {q.OpenserveAddress} (AMID {q.AmId}), but the order can't be submitted: {q.EligibilityBlocker ?? q.StatusLabel}. Nothing was sent.";
        return Result<OpenserveOrderFulfilmentDto>.Success(after, message);
    }

    public async Task<Result<OpenserveOrderFulfilmentDto>> AcceptAddressAsync(Guid orderId, string? note, CancellationToken cancellationToken = default)
    {
        var before = await BuildAsync(orderId, cancellationToken);
        if (before is null) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");
        if (!before.AppliesToOrder) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Address review only applies to Fibre orders.");
        if (!before.Qualification.CanAcceptAddress)
            return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.VALIDATION_ERROR, before.Qualification.CannotAcceptAddressReason ?? "Openserve's address can't be accepted for this order.");

        var trimmed = Clean(note);
        if (trimmed is null || trimmed.Length < MinAddressAcceptanceNoteLength)
            return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.VALIDATION_ERROR,
                $"Add a note (at least {MinAddressAcceptanceNoteLength} characters) saying how you confirmed that Openserve's address is the customer's property.");

        var evidence = await _dbContext.OpenserveQualificationResults.FirstAsync(r => r.Id == before.Qualification.EvidenceId, cancellationToken);
        var now = DateTime.UtcNow;
        evidence.AddressAcceptedAtUtc = now;
        evidence.AddressAcceptedByUserId = _currentUser.UserId;
        evidence.AddressAcceptanceNote = trimmed;
        evidence.UpdatedAtUtc = now;
        await _dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser.UserId,
                ActorType = _currentUser.UserId.HasValue ? AuditActorType.Admin : AuditActorType.System,
                ActionType = AuditActionType.OpenserveAddressAccepted,
                EntityType = AuditEntityType.Order,
                EntityId = orderId,
                EntityName = before.OrderNumber,
                Summary = $"Openserve address {evidence.CanonicalAddress} (AMID {evidence.Amid}) accepted as the customer's property for order {before.OrderNumber}.",
                MetadataJson = JsonSerializer.Serialize(new
                {
                    evidenceId = evidence.Id,
                    amid = evidence.Amid,
                    openserveAddress = evidence.CanonicalAddress,
                    customerAddress = evidence.CustomerAddress,
                    previousMatch = evidence.AddressMatch.ToString(),
                    detail = evidence.AddressMatchDetail,
                    reason = trimmed
                }),
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Openserve address acceptance audit write failed for order {OrderNumber}.", before.OrderNumber);
        }

        var after = await BuildAsync(orderId, cancellationToken);
        return Result<OpenserveOrderFulfilmentDto>.Success(after!, "Openserve's address accepted for this order. Nothing was sent — use Send/Retry when ready.");
    }

    public async Task<Result<OpenserveOrderFulfilmentDto>> SelectBuildingUnitAsync(Guid orderId, string? bldNumId, CancellationToken cancellationToken = default)
    {
        var before = await BuildAsync(orderId, cancellationToken);
        if (before is null) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");
        if (!before.AppliesToOrder) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Building/unit selection only applies to Fibre orders.");
        if (!before.Qualification.CanSelectBuilding)
            return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.VALIDATION_ERROR, before.Qualification.CannotSelectBuildingReason ?? "The building/unit can't be changed for this order.");

        var order = await _dbContext.Orders.FirstAsync(o => o.Id == orderId, cancellationToken);
        var wanted = bldNumId?.Trim();
        var matches = OpenserveBuildingCandidates.Read(order.OpenserveBuildingCandidatesJson)
            .Where(c => !string.IsNullOrWhiteSpace(wanted) && string.Equals(c.BldNumId?.Trim(), wanted, StringComparison.Ordinal))
            .ToList();
        // Only a row Openserve actually returned for this address — never a typed or guessed value.
        if (matches.Count != 1)
            return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "That building/unit is not one of the rows Openserve returned for this address.");

        var chosen = matches[0];
        var previous = order.OpenserveBuildingNumId;
        order.OpenserveBuildingNumId = chosen.BldNumId;
        order.OpenserveBuildingName = chosen.BuildingName;
        order.OpenserveFloor = chosen.Floor;
        order.OpenserveUnit = chosen.Num;
        order.OpenserveQualificationFailureReason = null; // the "pending unit confirmation" note no longer applies
        order.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser.UserId,
                ActorType = _currentUser.UserId.HasValue ? AuditActorType.Admin : AuditActorType.System,
                ActionType = AuditActionType.OpenserveBuildingUnitSelected,
                EntityType = AuditEntityType.Order,
                EntityId = order.Id,
                EntityName = order.OrderNumber,
                Summary = $"Openserve building/unit for order {order.OrderNumber} set to {DescribeCandidate(chosen)}.",
                MetadataJson = JsonSerializer.Serialize(new { bldNumId = chosen.BldNumId, num = chosen.Num, buildingName = chosen.BuildingName, floor = chosen.Floor, previousBldNumId = previous }),
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Openserve building/unit audit write failed for order {OrderNumber}.", order.OrderNumber);
        }

        var after = await BuildAsync(orderId, cancellationToken);
        return Result<OpenserveOrderFulfilmentDto>.Success(after!, $"Building/unit set to {DescribeCandidate(chosen)}. Nothing was sent — use Send/Retry when ready.");
    }

    public async Task<Result<OpenserveOrderFulfilmentDto>> RefreshBuildingCandidatesAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var before = await BuildAsync(orderId, cancellationToken);
        if (before is null) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");
        if (!before.AppliesToOrder) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Product Qualification only applies to Fibre orders.");
        if (!before.Qualification.CanRefreshBuildingCandidates)
            return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.VALIDATION_ERROR, before.Qualification.CannotRefreshBuildingCandidatesReason ?? "Building/unit rows can't be reloaded for this order.");

        var run = await _qualification.RefreshBuildingCandidatesAsync(orderId, cancellationToken);
        var after = await BuildAsync(orderId, cancellationToken);
        return run.Status == OpenserveQualificationRunStatus.Qualified
            ? Result<OpenserveOrderFulfilmentDto>.Success(after!, run.Message)
            : Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.VALIDATION_ERROR, run.Message);
    }

    private static string DescribeCandidate(OpenserveQualificationBuilding c) =>
        string.Join(" · ", new[] { c.BuildingName, c.Floor, string.IsNullOrWhiteSpace(c.Num) ? null : $"Unit {c.Num}", $"BLD_NUM_ID {c.BldNumId}" }.Where(s => !string.IsNullOrWhiteSpace(s)));

    // ─── read model ─────────────────────────────────────────────────

    private async Task<OpenserveOrderFulfilmentDto?> BuildAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await _dbContext.Orders.AsNoTracking().Include(o => o.ServicePackage).FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null) return null;

        var settings = _configProvider.Current;
        var recovery = settings.SubmissionRecovery;
        var now = DateTime.UtcNow;

        var dto = new OpenserveOrderFulfilmentDto
        {
            OrderId = order.Id,
            OrderNumber = order.OrderNumber,
            PackageName = order.PackageName,
            OrderStatus = order.Status.ToString(),
            InstallationAddress = FormatAddress(order),
            AppliesToOrder = order.PackageType == ServicePackageType.Fibre,
            IntegrationEnabled = settings.Enabled,
            AmId = order.OpenserveAmId,
            BuildingNumId = order.OpenserveBuildingNumId,
            MaxAutomaticRetries = recovery.MaxAttempts
        };

        if (!dto.AppliesToOrder)
        {
            dto.State = OpenserveFulfilmentState.NotApplicable;
            dto.StateLabel = "NOT APPLICABLE";
            dto.StateReason = "Only Fibre orders are fulfilled through Openserve.";
            dto.ManualSubmission = new OpenserveManualSubmissionDto { Allowed = false, Reason = dto.StateReason };
            dto.AutomaticRetry = new OpenserveFulfilmentPermissionDto { Allowed = false, Reason = dto.StateReason };
            return dto;
        }

        var record = await FindSalesOrderAsync(order.Id, cancellationToken);
        var accounts = await _dbContext.NetworkAccounts.AsNoTracking().Where(n => n.OrderId == order.Id).ToListAsync(cancellationToken);
        var account = OpenserveSubmissionRules.PickNetworkAccount(accounts);
        var currentMapping = order.ServicePackageId is null
            ? null
            : await _dbContext.PackageOpenserveMappings.AsNoTracking().FirstOrDefaultAsync(m => m.ServicePackageId == order.ServicePackageId && m.IsEnabled, cancellationToken);
        var recordMapping = record?.PackageOpenserveMappingId is { } mappingId
            ? await _dbContext.PackageOpenserveMappings.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mappingId, cancellationToken)
            : null;

        var forwarded = record is not null && OpenserveSubmissionRules.IsForwarded(record);
        var shownMapping = forwarded ? recordMapping ?? currentMapping : currentMapping;
        dto.ProductName = shownMapping?.OpenserveProductName;
        dto.Sku = shownMapping?.Sku;
        dto.Capacity = shownMapping?.Capacity;
        dto.CapacityUom = shownMapping?.CapacityUom;
        dto.HasEnabledMapping = currentMapping is not null;
        dto.SubscriberReferenceNumber = record?.SubscriberReferenceNumber ?? account?.OpenserveSubscriberReferenceNumber;
        dto.ForwardedToOpenserve = forwarded;

        if (record is not null)
        {
            dto.OpenserveOrderRecordId = record.Id;
            dto.ExternalReferenceNumber = record.ExternalReferenceNumber;
            dto.OpenserveOrderId = record.OpenserveOrderId;
            dto.OpenserveOrderName = record.OpenserveOrderName;
            dto.NormalizedStatus = record.NormalizedStatus.ToString();
            dto.RawState = record.RawState;
            dto.IsTerminal = record.IsTerminal;
            dto.LastSubmissionAttemptAtUtc = record.LastSubmissionAttemptAtUtc ?? (record.RetryCount > 0 ? record.UpdatedAtUtc : null);
            dto.LastSubmissionTrigger = record.LastSubmissionTrigger?.ToString();
            dto.SubmittedAtUtc = record.SubmittedAtUtc;
            dto.LastSuccessfulSyncAtUtc = record.LastSuccessfulSyncAtUtc;
            dto.LastOpenserveUpdateAtUtc = record.LastOpenserveUpdateAtUtc;
            dto.AttemptCount = record.RetryCount;
            dto.AutomaticRetryCount = record.AutomaticRetryCount;
            dto.LastFailureClass = record.LastFailureClass.ToString();
            dto.LastFailureCode = record.LastFailureCode;
            dto.LastFailureMessage = record.LastFailureMessage;
            dto.FailureClassExplanation = record.LastFailureClass == OpenserveSubmissionFailureClass.None ? null : OpenserveSubmissionFailureClassifier.ExplainClass(record.LastFailureClass);
            dto.NextAutomaticRetryAtUtc = record.NextAutomaticRetryAtUtc;
        }

        var staleClaim = record is not null && OpenserveSubmissionRules.IsStaleSubmitting(record, now, recovery.StaleSubmissionMinutes);
        var coordinatesAvailable = await _qualification.HasUsableCoordinatesAsync(order, cancellationToken);
        var evidence = await OpenserveSubmissionRules.LoadEvidenceAsync(_dbContext, order.OpenserveQualificationResultId, cancellationToken);
        var blocker = OpenserveSubmissionRules.PreflightBlocker(order, currentMapping, settings, coordinatesAvailable, evidence);
        var gate = OpenserveSubmissionRules.OrderGateReason(order);

        dto.Qualification = QualificationState(order, forwarded, coordinatesAvailable, settings, evidence, currentMapping);
        await ApplyBuildingSelectionAsync(dto.Qualification, order, cancellationToken);
        await ApplyAddressAcceptanceAsync(dto.Qualification, evidence, cancellationToken);
        dto.ServicePremises = await ServicePremisesAsync(order, evidence, cancellationToken);

        ApplyState(dto, order, record, account, blocker, staleClaim, settings);
        dto.ManualSubmission = ManualSubmission(order, record, account, blocker, gate, staleClaim, settings, now);
        dto.AutomaticRetry = await AutomaticRetryAsync(order, record, account, blocker, gate, staleClaim, settings, now, cancellationToken);
        dto.Automation = await AutomationStateAsync(order, forwarded, cancellationToken);
        dto.Activity = await ActivityAsync(order, record, cancellationToken);
        return dto;
    }

    private static void ApplyState(OpenserveOrderFulfilmentDto dto, Order order, OpenserveOrder? record, NetworkAccount? account, (string Code, string Reason)? blocker, bool staleClaim,
        OpenserveFulfilmentSettings settings)
    {
        void Set(string state, string label, string? reason)
        {
            dto.State = state;
            dto.StateLabel = label;
            dto.StateReason = reason;
        }

        if (record is not null && OpenserveSubmissionRules.IsForwarded(record))
        {
            var reference = record.OpenserveOrderName ?? record.OpenserveOrderId ?? record.ExternalReferenceNumber;
            switch (record.NormalizedStatus)
            {
                case OpenserveProvisioningStatus.Completed: Set(OpenserveFulfilmentState.Completed, "COMPLETED", $"Openserve completed order {reference}."); break;
                case OpenserveProvisioningStatus.Cancelled: Set(OpenserveFulfilmentState.Cancelled, "CANCELLED", $"Openserve order {reference} was cancelled."); break;
                case OpenserveProvisioningStatus.InProgress:
                case OpenserveProvisioningStatus.AwaitingCancellation:
                    Set(OpenserveFulfilmentState.InProgress, "IN PROGRESS", $"Openserve is working on order {reference} ({record.RawState ?? record.NormalizedStatus.ToString()})."); break;
                default: Set(OpenserveFulfilmentState.Submitted, "SUBMITTED", $"Openserve accepted order {reference}. Status updates arrive by callback and reconciliation."); break;
            }
            return;
        }

        if (order.Status is Shared.Enums.Orders.OrderStatus.Cancelled or Shared.Enums.Orders.OrderStatus.Rejected or Shared.Enums.Orders.OrderStatus.Failed)
        {
            Set(OpenserveFulfilmentState.OrderCancelled, "ORDER CANCELLED — NOT SENT", OpenserveSubmissionRules.OrderGateReason(order));
            return;
        }
        if (!settings.Enabled)
        {
            Set(OpenserveFulfilmentState.IntegrationDisabled, "OPENSERVE INTEGRATION DISABLED", "Openserve integration is disabled. Nothing is sent to Openserve — automatically or manually — until it is enabled.");
            return;
        }
        if (order.OpenserveAutomationPaused)
        {
            Set(OpenserveFulfilmentState.BlockedAdmin, "BLOCKED — ADMIN", "Openserve automation is paused for this order by an Admin. Nothing will be sent until it is resumed.");
            return;
        }

        if (record is not null)
        {
            if (record.NormalizedStatus == OpenserveProvisioningStatus.Submitting && !staleClaim)
            {
                Set(OpenserveFulfilmentState.SubmissionPending, "SUBMISSION PENDING", "A submission attempt is in progress.");
                return;
            }
            if (staleClaim)
            {
                Set(OpenserveFulfilmentState.FailedOutcomeUnknown, "FAILED — CHECK WITH OPENSERVE", OpenserveSubmissionFailureClassifier.Classify(null, OpenserveApiErrorCodes.Interrupted).Explanation);
                return;
            }

            var message = record.LastFailureMessage;
            switch (record.LastFailureClass)
            {
                case OpenserveSubmissionFailureClass.Retryable:
                    Set(OpenserveFulfilmentState.FailedRetryable, "FAILED — RETRYABLE", message); return;
                case OpenserveSubmissionFailureClass.OutcomeUnknown:
                    Set(OpenserveFulfilmentState.FailedOutcomeUnknown, "FAILED — CHECK WITH OPENSERVE", message); return;
                case OpenserveSubmissionFailureClass.Blocked:
                    var code = OpenserveSubmissionRules.InferBlockedCode(record.LastFailureCode, record.LastFailureMessage);
                    var resolvedNote = blocker is null ? " This now looks resolved — use Retry Openserve Submission to send it." : string.Empty;
                    var (state, label) = BlockedState(code);
                    Set(state, label, message + resolvedNote);
                    return;
                default:
                    Set(OpenserveFulfilmentState.FailedRejected, "FAILED — REJECTED BY OPENSERVE", message); return;
            }
        }

        // No submission record yet.
        if (!OpenserveSubmissionRules.SubmittableOrderStatuses.Contains(order.Status))
        {
            Set(OpenserveFulfilmentState.AwaitingPayment, "NOT SUBMITTED — AWAITING PAYMENT", OpenserveSubmissionRules.OrderGateReason(order));
            return;
        }
        if (account is null)
        {
            Set(OpenserveFulfilmentState.NotSubmitted, "NOT SUBMITTED", "No network account has been reserved for this order yet, so it hasn't reached the Openserve submission point.");
            return;
        }
        if (blocker is { } b)
        {
            var (state, label) = BlockedState(b.Code);
            Set(state, label, b.Reason);
            return;
        }
        Set(OpenserveFulfilmentState.NotSubmitted, "NOT SUBMITTED", "This order has not been sent to Openserve. It was not picked up automatically (for example, the integration was disabled when payment landed).");
    }

    private static OpenserveQualificationStateDto QualificationState(Order order, bool forwarded, bool coordinatesAvailable, OpenserveFulfilmentSettings settings,
        OpenserveQualificationResult? evidence, PackageOpenserveMapping? mapping)
    {
        var hasAmid = !string.IsNullOrWhiteSpace(order.OpenserveAmId);
        var recorded = order.OpenserveQualificationFailureReason;

        // Same evidence rule as the submission gate: evidence that identified a
        // different AMID doesn't describe this order.
        var linked = evidence is not null && evidence.Id == order.OpenserveQualificationResultId ? evidence : null;
        var assessed = linked is not null && (!hasAmid || !linked.AddressIdentified || string.Equals(linked.Amid, order.OpenserveAmId, StringComparison.OrdinalIgnoreCase)) ? linked : null;
        var assessment = OpenserveFibreEligibility.Assess(assessed, mapping, order.ServicePackage?.DownloadSpeedMbps);
        var mapped = mapping is null ? null : $"{mapping.Sku} {mapping.Capacity} {mapping.CapacityUom}";
        var fibreStatus = assessment.Status(buildingUnitResolved: !OpenserveBuildingCandidates.NeedsResolution(order));
        var (status, statusLabel) = !hasAmid && assessment.State is not (OpenserveQualificationState.AddressUnresolved or OpenserveQualificationState.NoAddressCandidates)
            ? order.OpenserveQualifiedAtUtc is null && linked is null ? ("NotRun", "Not run")
                : linked is { CallSucceeded: true } ? ("Failed", "Address not identified — Openserve returned no AMID") : ("Failed", "Failed")
            : assessment.State == OpenserveQualificationState.NoEvidence ? ("EvidenceMissing", "AMID captured by the old nearest-address lookup — not verified (re-run address verification)")
            : fibreStatus switch
            {
                OpenserveFibreQualificationStatus.AddressUnresolved => ("AddressUnresolved", "Exact Openserve premises not established — Fibre at the customer's address is not yet known"),
                OpenserveFibreQualificationStatus.NoAddressCandidates => ("NoAddressCandidates", "No Openserve address records near the customer's location"),
                OpenserveFibreQualificationStatus.AddressReviewRequired => ("AddressReviewRequired", "Openserve record not confirmed as the customer's premises"),
                OpenserveFibreQualificationStatus.FtthUnavailable => ("FtthUnavailable", "Customer's Openserve premises established — Fibre NOT available there"),
                OpenserveFibreQualificationStatus.ProductUnavailable => ("ProductUnavailable", $"Fibre available, but the mapped product ({mapped ?? "no mapping"}) is not"),
                OpenserveFibreQualificationStatus.BuildingUnitRequired => ("BuildingUnitRequired", $"Fibre and {mapped} available — building/unit must be resolved"),
                OpenserveFibreQualificationStatus.Orderable => ("Orderable", $"Orderable — {mapped} available at the customer's premises"),
                OpenserveFibreQualificationStatus.QualificationFailed => ("Failed", "Failed"),
                _ => ("NotRun", "Not run")
            };

        var state = new OpenserveQualificationStateDto
        {
            Status = status,
            StatusLabel = statusLabel,
            QualifiedAtUtc = order.OpenserveQualifiedAtUtc,
            AmId = order.OpenserveAmId,
            BuildingNumId = order.OpenserveBuildingNumId,
            BuildingName = order.OpenserveBuildingName,
            Floor = order.OpenserveFloor,
            Unit = order.OpenserveUnit,
            FailureReason = hasAmid ? null : recorded,
            CoordinatesAvailable = coordinatesAvailable,
            PropertyType = order.PropertyType?.ToString(),
            BuildingComplexName = order.BuildingComplexName,
            UnitNumber = order.UnitNumber,
            // AMID and buildingNumId are separate: an AMID is stored even when
            // several building/unit candidates came back. The unit then still
            // needs resolving, which is never guessed.
            BuildingResolution = !hasAmid ? "NotApplicable"
                : OpenserveBuildingCandidates.NeedsResolution(order) ? "NeedsResolution"
                : !string.IsNullOrWhiteSpace(order.OpenserveBuildingNumId) ? "Resolved"
                : "NotRequired",
            BuildingNote = hasAmid && string.IsNullOrWhiteSpace(order.OpenserveBuildingNumId) ? recorded : null,
            BuildingCandidateCount = order.OpenserveBuildingCandidateCount
        };

        ApplyEvidence(state, order, linked, assessment, mapping, hasAmid);

        var candidates = OpenserveBuildingCandidates.Read(order.OpenserveBuildingCandidatesJson);
        state.BuildingCandidates = candidates.Select(c => new OpenserveBuildingCandidateDto
        {
            BldNumId = c.BldNumId,
            BldId = c.BldId,
            FloorId = c.FloorId,
            Num = c.Num,
            BuildingName = c.BuildingName,
            Floor = c.Floor,
            IsSelected = !string.IsNullOrWhiteSpace(order.OpenserveBuildingNumId) && string.Equals(c.BldNumId, order.OpenserveBuildingNumId, StringComparison.Ordinal),
            MatchesCustomerUnit = OpenserveBuildingMatcher.UnitMatches(c, order.UnitNumber)
        }).ToList();

        var missingConfig = new[] { (settings.BaseUrl, "Base URL"), (settings.ApiKey, "API key"), (settings.WsIspCode, "ws-ispcode") }
            .Where(x => string.IsNullOrWhiteSpace(x.Item1)).Select(x => x.Item2).ToList();
        // Re-running is allowed until the order is with Openserve: an AMID is
        // not Fibre coverage, and corrected coordinates/address need a fresh answer.
        state.CannotRunReason = forwarded ? "Already with Openserve. Qualification cannot change a submitted order."
            : !settings.Enabled ? "Openserve integration is disabled."
            : missingConfig.Count > 0 ? $"Openserve configuration is incomplete (missing: {string.Join(", ", missingConfig)})."
            : order.Status is Shared.Enums.Orders.OrderStatus.Cancelled or Shared.Enums.Orders.OrderStatus.Rejected or Shared.Enums.Orders.OrderStatus.Failed
                ? $"The SmartFuture order is {order.Status}."
            : !coordinatesAvailable ? OpenserveQualificationService.MissingCoordinatesReason
            : null;
        state.CanRun = state.CannotRunReason is null;
        state.RunLabel = hasAmid || linked is not null ? "Re-run address verification & qualification" : "Run address verification & qualification";

        var orderIsClosed = order.Status is Shared.Enums.Orders.OrderStatus.Cancelled or Shared.Enums.Orders.OrderStatus.Rejected or Shared.Enums.Orders.OrderStatus.Failed;
        state.CannotAcceptAddressReason = assessed is null || !assessed.AddressIdentified ? "Run Product Qualification first — there is no Openserve address to accept."
            : forwarded ? "Already with Openserve."
            : orderIsClosed ? $"The SmartFuture order is {order.Status}."
            : assessed.AddressAcceptedAtUtc is not null ? "Openserve's address has already been accepted for this order."
            : assessed.AddressMatch == OpenserveAddressMatch.Matched ? "Openserve's address already matches the customer's address."
            : assessed.AddressMatch == OpenserveAddressMatch.NotEvaluated ? "The addresses haven't been compared yet — re-run Product Qualification."
            : null;
        state.CanAcceptAddress = state.CannotAcceptAddressReason is null;

        var resolution = linked?.AddressResolution ?? OpenserveAddressResolution.NotEvaluated;
        state.CannotSelectAddressCandidateReason = linked is null || state.AddressCandidates.Count == 0
                ? "No Openserve address candidates are recorded for this order — re-run address verification first."
            : forwarded ? "Already with Openserve. The premises can't change on a submitted order."
            : !settings.Enabled ? "Openserve integration is disabled."
            : orderIsClosed ? $"The SmartFuture order is {order.Status}."
            : resolution == OpenserveAddressResolution.AutoMatched && hasAmid ? "An Openserve record already matches the customer's street number and street — it was selected automatically."
            : null;
        state.CanSelectAddressCandidate = state.CannotSelectAddressCandidateReason is null;

        var orderClosed = order.Status is Shared.Enums.Orders.OrderStatus.Cancelled or Shared.Enums.Orders.OrderStatus.Rejected or Shared.Enums.Orders.OrderStatus.Failed;
        state.CannotSelectBuildingReason = !hasAmid ? "Run Product Qualification first — building/unit rows come with the AMID."
            : forwarded ? "Already with Openserve. The building/unit can't change on a submitted order."
            : orderClosed ? $"The SmartFuture order is {order.Status}."
            : candidates.Count == 0
                ? OpenserveBuildingCandidates.NeedsResolution(order)
                    ? "Openserve's building/unit rows weren't stored for this order. Reload them from Openserve first."
                    : "Openserve returned no building/unit rows for this address — nothing to choose."
            : null;
        state.CanSelectBuilding = state.CannotSelectBuildingReason is null;

        state.CannotRefreshBuildingCandidatesReason = !hasAmid ? "Run Product Qualification first."
            : forwarded ? "Already with Openserve."
            : !settings.Enabled ? "Openserve integration is disabled."
            : missingConfig.Count > 0 ? $"Openserve configuration is incomplete (missing: {string.Join(", ", missingConfig)})."
            : orderClosed ? $"The SmartFuture order is {order.Status}."
            : null;
        state.CanRefreshBuildingCandidates = state.CannotRefreshBuildingCandidatesReason is null;
        return state;
    }

    /// <summary>What Product Qualification actually said — address, Fibre, products, eligibility — so Admin never has to read raw logs.</summary>
    private static void ApplyEvidence(OpenserveQualificationStateDto state, Order order, OpenserveQualificationResult? evidence, OpenserveEligibilityAssessment assessment,
        PackageOpenserveMapping? mapping, bool hasAmid)
    {
        state.MappedProduct = mapping is null ? null : $"{mapping.Sku} {mapping.Capacity} {mapping.CapacityUom}";
        state.CustomerAddress = evidence?.CustomerAddress ?? FormatAddress(order);
        state.CustomerLatitude = evidence?.QueryLatitude ?? order.Latitude;
        state.CustomerLongitude = evidence?.QueryLongitude ?? order.Longitude;
        state.Eligible = assessment.IsEligible;
        state.Orderable = assessment.IsEligible && !OpenserveBuildingCandidates.NeedsResolution(order);
        var unresolved = assessment.State is OpenserveQualificationState.AddressUnresolved or OpenserveQualificationState.NoAddressCandidates;
        state.EligibilityBlocker = hasAmid || unresolved ? assessment.Blocker?.Reason : null;
        if (evidence is null) return;

        state.AddressResolution = evidence.AddressResolution.ToString();
        state.AddressResolutionLabel = evidence.AddressResolution switch
        {
            OpenserveAddressResolution.AutoMatched => "Matched automatically (street number + street)",
            OpenserveAddressResolution.AdminSelected => "Chosen by Admin",
            OpenserveAddressResolution.CustomerSelected => "Chosen by the customer (confirmed) — no automatic match",
            OpenserveAddressResolution.Unresolved => "Not established — no Openserve record matched",
            OpenserveAddressResolution.NoCandidates => "No Openserve address records found",
            _ => "Not verified (AMID from the old nearest-address lookup)"
        };
        state.AddressResolutionDetail = evidence.AddressResolutionDetail;
        state.AddressVerifiedAtUtc = evidence.AddressVerifiedAtUtc;
        state.AddressResolvedAtUtc = evidence.AddressResolvedAtUtc;
        state.AddressResolutionNote = evidence.AddressResolutionNote;
        state.AddressCandidates = OpenserveAddressCandidateMatcher.Read(evidence.AddressCandidatesJson).Select(c => new OpenserveAddressCandidateDto
        {
            Amid = c.Amid,
            Address = c.Address,
            DistanceMeters = c.DistanceMeters,
            DistanceText = c.DistanceText,
            Latitude = c.Latitude,
            Longitude = c.Longitude,
            Match = c.Match.ToString(),
            MatchLabel = c.Match switch
            {
                OpenserveCandidateMatch.Matched => "Matches the customer's address",
                OpenserveCandidateMatch.StreetNumberMismatch => "Street number mismatch",
                OpenserveCandidateMatch.StreetMismatch => "Street mismatch",
                OpenserveCandidateMatch.LocalityMismatch => "Area mismatch",
                _ => "Can't compare"
            },
            MatchDetail = c.MatchDetail,
            IsSelected = !string.IsNullOrWhiteSpace(order.OpenserveAmId) && string.Equals(c.Amid, order.OpenserveAmId, StringComparison.Ordinal)
        }).ToList();

        state.EvidenceId = evidence.Id;
        state.EvidenceSource = evidence.Purpose.ToString();
        state.AddressIdentified = evidence.CallSucceeded && evidence.AddressIdentified;
        state.OpenserveAddress = evidence.CanonicalAddress;
        state.DistanceMeters = evidence.DistanceMeters;
        state.DistanceText = evidence.DistanceText;
        state.Region = evidence.Region;
        state.AddressStatus = evidence.AddressStatus;
        state.MduVerification = evidence.MduVerification;
        state.AddressMatch = evidence.AddressMatch.ToString();
        state.AddressMatchDetail = evidence.AddressMatchDetail;
        state.AddressAccepted = evidence.AddressAcceptedAtUtc is not null;
        state.AddressAcceptedAtUtc = evidence.AddressAcceptedAtUtc;
        state.AddressAcceptanceNote = evidence.AddressAcceptanceNote;
        state.FibreAvailability = evidence.CallSucceeded ? evidence.FibreAvailability.ToString() : nameof(OpenserveFibreAvailability.NotEvaluated);
        state.FibreAvailabilityLabel = !evidence.CallSucceeded ? "Not evaluated (qualification failed)" : evidence.FibreAvailability switch
        {
            OpenserveFibreAvailability.Available => "Available",
            OpenserveFibreAvailability.NotYetAvailable => "Not yet available",
            OpenserveFibreAvailability.NotReturned => "Not available — Openserve returned no FTTH infrastructure",
            _ => "Not evaluated"
        };
        state.FtthStatus = evidence.FtthStatusSummary;
        state.FibreMaxSpeedMbps = evidence.FibreMaxSpeedMbps;
        state.EthernetProductCodes = evidence.EthernetProductCodes;
        state.FwaStatus = evidence.FwaStatus;
        state.Infrastructures = evidence.Products
            .GroupBy(p => p.InfrastructureIndex)
            .OrderBy(g => g.Key)
            .Select(g => new OpenserveQualificationInfrastructureDto
            {
                Index = g.Key,
                Network = string.IsNullOrWhiteSpace(g.First().InfrastructureType) ? "Openserve network" : g.First().InfrastructureType!,
                FtthStatus = g.First().FtthStatus,
                ImmediatelyAvailable = g.First().IsImmediatelyAvailable,
                MaxSpeedMbps = g.First().FibreMaxSpeedMbps,
                ServiceProviderId = g.First().ServiceProviderId,
                Products = g.Where(p => p.ProductCode is not null).Select(p => new OpenserveQualificationProductDto
                {
                    ProductCode = p.ProductCode,
                    ProductName = p.ProductName,
                    UpstreamSpeed = p.UpstreamSpeed,
                    DownstreamSpeed = p.DownstreamSpeed,
                    IsMappedProduct = mapping is not null && string.Equals(p.ProductCode, mapping.Sku, StringComparison.OrdinalIgnoreCase)
                }).ToList()
            })
            .ToList();
        state.ProductEligibility = assessment.State == OpenserveQualificationState.Evaluated ? assessment.Product.ToString() : nameof(OpenserveProductEligibility.NotEvaluated);
        state.ProductEligibilityReason = assessment.State == OpenserveQualificationState.Evaluated ? assessment.ProductReason : null;
    }

    /// <summary>Who accepted Openserve's address / chose the premises, when an Admin did.</summary>
    private async Task ApplyAddressAcceptanceAsync(OpenserveQualificationStateDto state, OpenserveQualificationResult? evidence, CancellationToken cancellationToken)
    {
        if (evidence?.AddressAcceptedByUserId is { } acceptedBy) state.AddressAcceptedBy = await UserNameAsync(acceptedBy, cancellationToken);
        if (evidence?.AddressResolvedByUserId is { } resolvedBy) state.AddressResolvedBy = await UserNameAsync(resolvedBy, cancellationToken);
    }

    /// <summary>
    /// The order's Openserve service premises next to its installation address: the snapshot kept on the order
    /// (who/what established it, when, distance, customer confirmation), falling back to the linked evidence for
    /// orders qualified before the snapshot existed.
    /// </summary>
    private async Task<OpenserveServicePremisesDto> ServicePremisesAsync(Order order, OpenserveQualificationResult? evidence, CancellationToken cancellationToken)
    {
        var premises = new OpenserveServicePremisesDto();
        if (string.IsNullOrWhiteSpace(order.OpenserveAmId)) return premises;

        var linked = evidence is not null && evidence.Id == order.OpenserveQualificationResultId && string.Equals(evidence.Amid, order.OpenserveAmId, StringComparison.OrdinalIgnoreCase)
            ? evidence : null;
        var selection = order.OpenservePremisesSelection != OpenserveAddressResolution.NotEvaluated ? order.OpenservePremisesSelection
            : linked?.AddressResolution ?? OpenserveAddressResolution.NotEvaluated;
        var byUserId = order.OpenservePremisesSelectedByUserId ?? (selection is OpenserveAddressResolution.CustomerSelected or OpenserveAddressResolution.AdminSelected ? linked?.AddressResolvedByUserId : null);
        var selectedBy = byUserId is { } id ? await UserNameAsync(id, cancellationToken)
            : selection == OpenserveAddressResolution.CustomerSelected ? "Customer (website, not signed in)" : null;

        premises.Established = true;
        premises.Amid = order.OpenserveAmId;
        premises.Address = order.OpenservePremisesAddress ?? linked?.CanonicalAddress;
        premises.Selection = selection.ToString();
        premises.SelectedBy = selectedBy;
        premises.SelectedAtUtc = order.OpenservePremisesSelectedAtUtc ?? linked?.AddressResolvedAtUtc;
        premises.DistanceMeters = order.OpenservePremisesDistanceMeters
            ?? OpenserveAddressCandidateMatcher.Read(linked?.AddressCandidatesJson).FirstOrDefault(c => string.Equals(c.Amid, order.OpenserveAmId, StringComparison.Ordinal))?.DistanceMeters;
        premises.CustomerConfirmedAtUtc = order.OpenservePremisesCustomerConfirmedAtUtc ?? (selection == OpenserveAddressResolution.CustomerSelected ? linked?.AddressResolvedAtUtc : null);
        premises.CustomerConfirmed = premises.CustomerConfirmedAtUtc is not null;
        premises.SelectionLabel = selection switch
        {
            OpenserveAddressResolution.AutoMatched => "Selected automatically — exact address match",
            OpenserveAddressResolution.CustomerSelected => "Selected by the customer (confirmed) — no automatic match",
            OpenserveAddressResolution.AdminSelected => $"Selected manually by {selectedBy ?? "Admin"}",
            _ => "Nearest-address lookup (before address verification) — not verified"
        };
        premises.DiffersFromInstallationAddress = selection != OpenserveAddressResolution.AutoMatched && linked?.AddressMatch != OpenserveAddressMatch.Matched;
        return premises;
    }

    private async Task<string> UserNameAsync(Guid userId, CancellationToken cancellationToken) =>
        await _dbContext.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => (u.FirstName + " " + u.LastName).Trim() == string.Empty ? u.Email : (u.FirstName + " " + u.LastName).Trim())
            .FirstOrDefaultAsync(cancellationToken) ?? "Admin";

    /// <summary>Who chose the current building/unit, when an Admin did (from the audit log).</summary>
    private async Task ApplyBuildingSelectionAsync(OpenserveQualificationStateDto state, Order order, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(order.OpenserveBuildingNumId)) return;
        var latest = await _dbContext.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == AuditEntityType.Order && a.EntityId == order.Id && a.ActionType == AuditActionType.OpenserveBuildingUnitSelected)
            .OrderByDescending(a => a.CreatedAtUtc)
            .Select(a => new
            {
                a.CreatedAtUtc,
                a.MetadataJson,
                Actor = a.ActorUser != null ? ((a.ActorUser.FirstName + " " + a.ActorUser.LastName).Trim() == string.Empty ? a.ActorUser.Email : (a.ActorUser.FirstName + " " + a.ActorUser.LastName).Trim()) : null
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is null || MetadataString(latest.MetadataJson, "bldNumId") != order.OpenserveBuildingNumId) return;
        state.BuildingSelectedBy = latest.Actor ?? "Admin";
        state.BuildingSelectedAtUtc = latest.CreatedAtUtc;
    }

    private static (string State, string Label) BlockedState(string code) => code switch
    {
        OpenserveBlockedCodes.Configuration => (OpenserveFulfilmentState.BlockedConfiguration, "BLOCKED — CONFIGURATION"),
        OpenserveBlockedCodes.Mapping => (OpenserveFulfilmentState.BlockedPackageMapping, "BLOCKED — PACKAGE MAPPING"),
        OpenserveBlockedCodes.BuildingUnit => (OpenserveFulfilmentState.BlockedBuildingUnit, "BLOCKED — BUILDING / UNIT DETAILS"),
        OpenserveBlockedCodes.Qualification => (OpenserveFulfilmentState.BlockedQualification, "BLOCKED — PRODUCT QUALIFICATION"),
        OpenserveBlockedCodes.AddressReview => (OpenserveFulfilmentState.BlockedAddressReview, "BLOCKED — ADDRESS REVIEW"),
        OpenserveBlockedCodes.AddressUnresolved => (OpenserveFulfilmentState.BlockedAddressUnresolved, "BLOCKED — ADDRESS NOT VERIFIED"),
        OpenserveBlockedCodes.FibreUnavailable => (OpenserveFulfilmentState.BlockedFibreUnavailable, "BLOCKED — FIBRE NOT AVAILABLE"),
        OpenserveBlockedCodes.ProductUnavailable => (OpenserveFulfilmentState.BlockedProductUnavailable, "BLOCKED — PRODUCT NOT AVAILABLE"),
        _ => (OpenserveFulfilmentState.BlockedOrderData, "BLOCKED — ORDER DETAILS")
    };

    private static OpenserveManualSubmissionDto ManualSubmission(Order order, OpenserveOrder? record, NetworkAccount? account, (string Code, string Reason)? blocker, string? gate, bool staleClaim,
        OpenserveFulfilmentSettings settings, DateTime now)
    {
        OpenserveManualSubmissionDto No(string reason) => new() { Allowed = false, Reason = reason };

        if (record is not null && OpenserveSubmissionRules.IsForwarded(record)) return No("Already submitted to Openserve — it is never sent twice. Use Synchronize to refresh its status.");
        if (!settings.Enabled) return No("Openserve integration is disabled.");
        if (gate is not null) return No(gate);
        if (account is null) return No("No network account has been reserved for this order yet (payment not applied).");
        if (record is not null && record.NormalizedStatus == OpenserveProvisioningStatus.Submitting && !staleClaim) return No("A submission attempt is already in progress.");
        if (blocker is { } b) return No(b.Reason);

        var requiresConfirmation = staleClaim || record?.LastFailureClass == OpenserveSubmissionFailureClass.OutcomeUnknown;
        return new OpenserveManualSubmissionDto
        {
            Allowed = true,
            Action = record is null ? "Send" : "Retry",
            RequiresOutcomeConfirmation = requiresConfirmation,
            Reason = requiresConfirmation
                ? $"Openserve may already have received this order. Confirm with Openserve that no order exists for reference {record!.ExternalReferenceNumber} before retrying."
                : record is null ? "Ready to send to Openserve." : "Safe to retry — the same SmartFuture order and references are reused."
        };
    }

    private async Task<OpenserveFulfilmentPermissionDto> AutomaticRetryAsync(Order order, OpenserveOrder? record, NetworkAccount? account, (string Code, string Reason)? blocker, string? gate, bool staleClaim,
        OpenserveFulfilmentSettings settings, DateTime now, CancellationToken cancellationToken)
    {
        OpenserveFulfilmentPermissionDto No(string reason) => new() { Allowed = false, Reason = reason };
        var recovery = settings.SubmissionRecovery;

        if (record is not null && OpenserveSubmissionRules.IsForwarded(record)) return No("Not needed — Openserve has the order. Status is tracked by reconciliation (GET), never by resubmitting.");
        if (!settings.Enabled) return No("Openserve integration is disabled.");
        if (!recovery.Enabled) return No("Automatic submission recovery is switched off (OpenserveFulfilment:SubmissionRecovery:Enabled).");
        if (gate is not null) return No(gate);

        if (record is null)
        {
            if (account is null) return No("The order hasn't reached the submission point (no network account reserved).");
            if (blocker is { } b) return No($"Blocked: {b.Reason}");
            if (!OpenserveSubmissionRules.SweepOrderStatuses.Contains(order.Status) || account.Status != NetworkAccountStatus.Pending)
                return No("The safety sweep only picks up orders whose installation hasn't happened yet — send it manually if Openserve still needs it.");

            var (floor, ceiling) = await OpenserveSubmissionRecoveryService.GetSafetySweepWindowAsync(_dbContext, recovery, now, cancellationToken);
            if (account.CreatedAtUtc < floor)
                return No("Outside the safety-sweep window (it became eligible before the integration was last enabled, or too long ago) — send it manually.");
            return new OpenserveFulfilmentPermissionDto
            {
                Allowed = true,
                Reason = account.CreatedAtUtc > ceiling
                    ? "The automatic trigger should handle it; the safety sweep picks it up if not."
                    : $"The safety sweep (every {Math.Clamp(recovery.SafetySweepIntervalHours, 1, 24)}h) will submit it."
            };
        }

        if (record.NormalizedStatus == OpenserveProvisioningStatus.Submitting && !staleClaim) return No("A submission attempt is in progress.");
        if (staleClaim) return No(OpenserveSubmissionFailureClassifier.ExplainClass(OpenserveSubmissionFailureClass.OutcomeUnknown));

        return record.LastFailureClass switch
        {
            OpenserveSubmissionFailureClass.Retryable when record.NextAutomaticRetryAtUtc is not null => new OpenserveFulfilmentPermissionDto
            {
                Allowed = true,
                Reason = $"Next automatic retry at {record.NextAutomaticRetryAtUtc:yyyy-MM-dd HH:mm} UTC (automatic retry {record.AutomaticRetryCount + 1} of {recovery.MaxAttempts})."
            },
            OpenserveSubmissionFailureClass.Retryable => No($"Automatic retries are used up ({record.AutomaticRetryCount} of {recovery.MaxAttempts}). Retry manually."),
            OpenserveSubmissionFailureClass.Blocked => No("Never sent: fix the blocker, then retry manually. Blocked submissions are not retried automatically."),
            OpenserveSubmissionFailureClass.OutcomeUnknown => No(OpenserveSubmissionFailureClassifier.ExplainClass(OpenserveSubmissionFailureClass.OutcomeUnknown)),
            _ => No(OpenserveSubmissionFailureClassifier.ExplainClass(OpenserveSubmissionFailureClass.NonRetryable))
        };
    }

    private async Task<OpenserveAutomationPauseStateDto> AutomationStateAsync(Order order, bool forwarded, CancellationToken cancellationToken)
    {
        string? pausedBy = null;
        if (order.OpenserveAutomationPaused && order.OpenserveAutomationPausedByUserId is { } userId)
        {
            pausedBy = await _dbContext.Users.AsNoTracking().Where(u => u.Id == userId)
                .Select(u => (u.FirstName + " " + u.LastName).Trim() == string.Empty ? u.Email : (u.FirstName + " " + u.LastName).Trim())
                .FirstOrDefaultAsync(cancellationToken);
        }

        return new OpenserveAutomationPauseStateDto
        {
            Paused = order.OpenserveAutomationPaused,
            PausedAtUtc = order.OpenserveAutomationPausedAtUtc,
            PausedBy = pausedBy,
            Reason = order.OpenserveAutomationPauseReason,
            CanPause = !order.OpenserveAutomationPaused && !forwarded,
            CanResume = order.OpenserveAutomationPaused,
            CannotPauseReason = order.OpenserveAutomationPaused ? null : forwarded ? "Already with Openserve — there is no submission left to pause." : null
        };
    }

    /// <summary>
    /// Merged from the existing records — integration logs (attempts, blocks,
    /// interruptions, failed status checks, cancellations), status history
    /// (Openserve status changes) and the audit log (pause/resume). No new table.
    /// </summary>
    private async Task<IReadOnlyList<OpenserveFulfilmentActivityDto>> ActivityAsync(Order order, OpenserveOrder? record, CancellationToken cancellationToken)
    {
        var items = new List<OpenserveFulfilmentActivityDto>();

        if (record is not null)
        {
            var logs = await _dbContext.OpenserveIntegrationLogs.AsNoTracking()
                .Where(l => l.OpenserveOrderId == record.Id && l.Direction == OpenserveIntegrationDirection.Outbound
                            && (l.OperationType == OpenserveOperationType.CreateOrder || l.OperationType == OpenserveOperationType.CancelOrder
                                || (l.OperationType == OpenserveOperationType.GetOrder && !l.IsSuccess)))
                .OrderByDescending(l => l.OccurredAtUtc)
                .Take(MaxActivityItems)
                .Select(l => new { l.OccurredAtUtc, l.OperationType, l.IsSuccess, l.ErrorSummary, l.ResponseStatusCode, l.SubmissionTrigger })
                .ToListAsync(cancellationToken);

            foreach (var l in logs)
            {
                var who = Capitalize(OpenserveOrderSubmissionService.TriggerLabel(l.SubmissionTrigger));
                var http = l.ResponseStatusCode is { } status ? $"HTTP {status} — " : string.Empty;
                items.Add(l.OperationType switch
                {
                    OpenserveOperationType.CreateOrder when l.IsSuccess => Item(l.OccurredAtUtc, "Submission", $"{who} — accepted by Openserve", null, "success"),
                    OpenserveOperationType.CreateOrder when l.ErrorSummary != null && l.ErrorSummary.StartsWith("BLOCKED", StringComparison.Ordinal) =>
                        Item(l.OccurredAtUtc, "Submission", $"{who} blocked — not sent", StripPrefix(l.ErrorSummary), "warning"),
                    OpenserveOperationType.CreateOrder when l.ErrorSummary != null && l.ErrorSummary.StartsWith("INTERRUPTED", StringComparison.Ordinal) =>
                        Item(l.OccurredAtUtc, "Submission", $"{who} interrupted — outcome unknown", StripPrefix(l.ErrorSummary), "failure"),
                    OpenserveOperationType.CreateOrder => Item(l.OccurredAtUtc, "Submission", $"{who} failed", http + l.ErrorSummary, "failure"),
                    OpenserveOperationType.CancelOrder => Item(l.OccurredAtUtc, "Cancellation", l.IsSuccess ? "Cancellation sent to Openserve" : "Cancellation request failed", l.IsSuccess ? null : http + l.ErrorSummary,
                        l.IsSuccess ? "info" : "failure"),
                    _ => Item(l.OccurredAtUtc, "Sync", "Status check with Openserve failed", http + l.ErrorSummary, "warning")
                });
            }

            var history = await _dbContext.OpenserveOrderStatusHistories.AsNoTracking()
                .Where(h => h.OpenserveOrderId == record.Id)
                .OrderByDescending(h => h.ReceivedAtUtc)
                .Take(MaxActivityItems)
                .Select(h => new { h.ReceivedAtUtc, h.NewRawState, h.NormalizedStatus, h.Description })
                .ToListAsync(cancellationToken);
            items.AddRange(history.Select(h => Item(h.ReceivedAtUtc, "Status", $"Openserve status: {h.NewRawState ?? h.NormalizedStatus.ToString()}", h.Description,
                h.NormalizedStatus == OpenserveProvisioningStatus.Completed ? "success" : "info")));
        }

        var automation = await _dbContext.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == AuditEntityType.Order && a.EntityId == order.Id
                        && (a.ActionType == AuditActionType.OpenserveAutomationPaused || a.ActionType == AuditActionType.OpenserveAutomationResumed
                            || a.ActionType == AuditActionType.OpenserveOrderQualificationRun || a.ActionType == AuditActionType.OpenserveBuildingUnitSelected
                            || a.ActionType == AuditActionType.OpenserveAddressAccepted || a.ActionType == AuditActionType.OpenserveAddressCandidateSelected))
            .OrderByDescending(a => a.CreatedAtUtc)
            .Take(MaxActivityItems)
            .Select(a => new
            {
                a.CreatedAtUtc,
                a.ActionType,
                a.MetadataJson,
                Actor = a.ActorUser != null ? ((a.ActorUser.FirstName + " " + a.ActorUser.LastName).Trim() == string.Empty ? a.ActorUser.Email : (a.ActorUser.FirstName + " " + a.ActorUser.LastName).Trim()) : null
            })
            .ToListAsync(cancellationToken);
        foreach (var a in automation)
        {
            if (a.ActionType == AuditActionType.OpenserveOrderQualificationRun)
            {
                items.Add(QualificationItem(a.CreatedAtUtc, a.MetadataJson, a.Actor));
                continue;
            }
            if (a.ActionType == AuditActionType.OpenserveAddressCandidateSelected)
            {
                items.Add(Item(a.CreatedAtUtc, "Qualification",
                    $"Openserve premises chosen{(a.Actor is null ? string.Empty : $" by {a.Actor}")} — {MetadataString(a.MetadataJson, "selectedAddress")} (AMID {MetadataString(a.MetadataJson, "selectedAmid")})",
                    ReasonFromMetadata(a.MetadataJson), "info"));
                continue;
            }
            if (a.ActionType == AuditActionType.OpenserveAddressAccepted)
            {
                items.Add(Item(a.CreatedAtUtc, "Qualification", $"Openserve address accepted as the customer's{(a.Actor is null ? string.Empty : $" by {a.Actor}")} — {MetadataString(a.MetadataJson, "openserveAddress")}",
                    ReasonFromMetadata(a.MetadataJson), "info"));
                continue;
            }
            if (a.ActionType == AuditActionType.OpenserveBuildingUnitSelected)
            {
                var num = MetadataString(a.MetadataJson, "num");
                var bld = MetadataString(a.MetadataJson, "bldNumId");
                items.Add(Item(a.CreatedAtUtc, "Qualification", $"Building/unit selected{(a.Actor is null ? string.Empty : $" by {a.Actor}")} — {(num is null ? string.Empty : $"Unit {num} ")}({bld})",
                    MetadataString(a.MetadataJson, "buildingName"), "info"));
                continue;
            }
            var paused = a.ActionType == AuditActionType.OpenserveAutomationPaused;
            var title = $"Openserve automation {(paused ? "paused" : "resumed")}{(a.Actor is null ? string.Empty : $" by {a.Actor}")}";
            items.Add(Item(a.CreatedAtUtc, "Automation", title, ReasonFromMetadata(a.MetadataJson), paused ? "warning" : "info"));
        }

        // The customer's choice is made before the order exists; its permanent record on the order is the premises snapshot.
        if (order.OpenservePremisesSelection == OpenserveAddressResolution.CustomerSelected && order.OpenservePremisesSelectedAtUtc is { } chosenAt)
        {
            items.Add(Item(chosenAt, "Qualification", $"Openserve service location chosen by the customer — {order.OpenservePremisesAddress ?? $"AMID {order.OpenserveAmId}"}",
                $"No Openserve record matched the installation address automatically; the customer confirmed this one corresponds to their property. Installation address unchanged: {FormatAddress(order)}.",
                "info"));
        }

        return items.OrderByDescending(i => i.OccurredAtUtc).Take(MaxActivityItems).ToList();
    }

    // ─── helpers ────────────────────────────────────────────────────

    private Task<OpenserveOrder?> FindSalesOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        _dbContext.OpenserveOrders.AsNoTracking()
            .Where(o => o.OrderId == orderId && o.OrderType == OpenserveSubmissionRules.SalesOrderType)
            .OrderBy(o => o.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task AuditAsync(AuditActionType actionType, Order order, string summary, string? reason)
    {
        try
        {
            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser.UserId,
                ActorType = _currentUser.UserId.HasValue ? AuditActorType.Admin : AuditActorType.System,
                ActionType = actionType,
                EntityType = AuditEntityType.Order,
                EntityId = order.Id,
                EntityName = order.OrderNumber,
                Summary = summary,
                MetadataJson = JsonSerializer.Serialize(new { reason }),
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Openserve automation audit write failed for order {OrderNumber}.", order.OrderNumber);
        }
    }

    private static OpenserveFulfilmentActivityDto Item(DateTime at, string kind, string title, string? detail, string tone) =>
        new() { OccurredAtUtc = at, Kind = kind, Title = title, Detail = string.IsNullOrWhiteSpace(detail) ? null : detail, Tone = tone };

    private static string? StripPrefix(string? summary)
    {
        if (summary is null) return null;
        var i = summary.IndexOf(": ", StringComparison.Ordinal);
        return i >= 0 ? summary[(i + 2)..] : summary;
    }

    private static OpenserveFulfilmentActivityDto QualificationItem(DateTime at, string? json, string? actor)
    {
        static string? Prop(JsonElement root, string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string? status = null, amid = null, reason = null, trigger = null, fibre = null, product = null, address = null, resolution = null;
        bool? eligible = null;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                status = Prop(doc.RootElement, "status");
                amid = Prop(doc.RootElement, "amid");
                reason = Prop(doc.RootElement, "reason");
                trigger = Prop(doc.RootElement, "trigger");
                fibre = Prop(doc.RootElement, "fibre");
                product = Prop(doc.RootElement, "productEligibility");
                address = Prop(doc.RootElement, "addressMatch");
                resolution = Prop(doc.RootElement, "addressResolution");
                if (doc.RootElement.TryGetProperty("eligible", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False) eligible = e.GetBoolean();
            }
            catch (JsonException)
            {
                // Unreadable metadata just yields a generic line.
            }
        }

        var who = trigger switch
        {
            nameof(OpenserveQualificationTrigger.PaymentConversion) => " (when the order was created)",
            nameof(OpenserveQualificationTrigger.SubmissionSelfHeal) => " (before submission)",
            nameof(OpenserveQualificationTrigger.BuildingCandidatesRefresh) => actor is null ? " (building/unit rows reloaded)" : $" — building/unit rows reloaded by {actor}",
            _ => actor is null ? string.Empty : $" by {actor}"
        };
        if (status == nameof(OpenserveQualificationRunStatus.AddressUnresolved))
            return Item(at, "Qualification", $"Address verification{who} — exact Openserve premises not established", reason, "warning");
        if (status != nameof(OpenserveQualificationRunStatus.Qualified)) return Item(at, "Qualification", $"Product Qualification{who} — no AMID", reason, "warning");

        // Older entries (before evidence was recorded) only know the AMID.
        if (eligible is null) return Item(at, "Qualification", $"Product Qualification{who} — AMID {amid} captured", reason, "success");
        var facts = string.Join(" · ", new[] { resolution is null ? null : $"Premises: {resolution}", fibre is null ? null : $"Fibre: {fibre}", address is null ? null : $"Address: {address}",
            product is null ? null : $"Product: {product}" }.Where(s => s is not null));
        return eligible.Value
            ? Item(at, "Qualification", $"Product Qualification{who} — AMID {amid}, eligible", facts, "success")
            : Item(at, "Qualification", $"Product Qualification{who} — AMID {amid}, NOT orderable", string.IsNullOrEmpty(facts) ? reason : facts, "warning");
    }

    private static string? MetadataString(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReasonFromMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Capitalize(string value) => string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static string? Clean(string? reason)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= MaxPauseReasonLength ? trimmed : trimmed[..MaxPauseReasonLength];
    }

    private static string FormatAddress(Order order) =>
        string.Join(", ", new[] { order.AddressLine1, order.AddressLine2, order.Suburb, order.City, order.Province, order.PostalCode }.Where(p => !string.IsNullOrWhiteSpace(p)));
}

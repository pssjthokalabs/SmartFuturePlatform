using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Errors;

namespace SmartFuture.Application.Openserve;

public class OpenserveQualificationService : IOpenserveQualificationService
{
    public const string MissingCoordinatesReason = "Product Qualification unavailable — installation coordinates are missing.";

    /// <summary>Customer wording when Openserve can't be asked right now — the checkout gate fails closed.</summary>
    public const string QualificationUnavailableMessage = "We couldn't confirm Fibre availability at your address right now. Please try again in a few minutes.";

    private const int MaxReuseCandidates = 200;

    private readonly IAppDbContext _dbContext;
    private readonly IOpenserveApiClient _client;
    private readonly IOpenserveRuntimeConfigProvider _configProvider;
    private readonly IAuditService? _auditService;
    private readonly ICurrentUserService? _currentUser;
    private readonly ILogger<OpenserveQualificationService> _logger;

    public OpenserveQualificationService(IAppDbContext dbContext, IOpenserveApiClient client, IOpenserveRuntimeConfigProvider configProvider, ILogger<OpenserveQualificationService> logger,
        IAuditService? auditService = null, ICurrentUserService? currentUser = null)
    {
        _dbContext = dbContext;
        _client = client;
        _configProvider = configProvider;
        _logger = logger;
        _auditService = auditService;
        _currentUser = currentUser;
    }

    public bool CanQualify => _configProvider.Current.CanQualify;

    public async Task QualifyOrderAsync(Order order, CancellationToken cancellationToken = default) =>
        await RunForOrderSafeAsync(order, OpenserveQualificationPurpose.OrderCreated, cancellationToken);

    public async Task QualifyOrderFromCheckoutAsync(Order order, Guid? checkoutEvidenceId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (checkoutEvidenceId is { } evidenceId && await TryApplyCheckoutEvidenceAsync(order, evidenceId, cancellationToken)) return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "[Openserve][qualify] Could not reuse checkout evidence {EvidenceId} for order {OrderNumber}; qualifying again.", checkoutEvidenceId, order.OrderNumber);
        }
        await QualifyOrderAsync(order, cancellationToken);
    }

    public async Task<OpenserveQualificationRunResult> QualifyAndPersistAsync(Guid orderId, OpenserveQualificationTrigger trigger, bool ignoreCooldown = false, CancellationToken cancellationToken = default)
    {
        try
        {
            // Decide from the database, not from an instance this context may
            // already be tracking (which can be stale, e.g. after another API
            // instance qualified the order).
            var current = await _dbContext.Orders.AsNoTracking()
                .Where(o => o.Id == orderId)
                .Select(o => new { o.PackageType, o.OpenserveAmId, o.OpenserveBuildingNumId, o.OpenserveQualifiedAtUtc, o.OpenserveQualificationFailureReason, o.OpenserveQualificationResultId })
                .FirstOrDefaultAsync(cancellationToken);
            if (current is null) return new OpenserveQualificationRunResult(OpenserveQualificationRunStatus.NotFound, "Order not found.");

            if (current.PackageType != ServicePackageType.Fibre)
                return Skipped("Not a Fibre order — Product Qualification does not apply.");

            var linked = current.OpenserveQualificationResultId is { } linkedId
                ? await _dbContext.OpenserveQualificationResults.AsNoTracking().Where(r => r.Id == linkedId)
                    .Select(r => new { r.Id, r.CallSucceeded, r.AddressIdentified, r.Amid, r.OrderId }).FirstOrDefaultAsync(cancellationToken)
                : null;
            var hasAmid = !string.IsNullOrWhiteSpace(current.OpenserveAmId);

            // Payment-first conversion: the checkout gate already qualified this
            // exact address — apply that evidence instead of calling again.
            if (!hasAmid && trigger != OpenserveQualificationTrigger.AdminManual && linked is { CallSucceeded: true } && (linked.OrderId is null || linked.OrderId == orderId))
            {
                var tracked = await _dbContext.Orders.FirstAsync(o => o.Id == orderId, cancellationToken);
                if (await TryApplyCheckoutEvidenceAsync(tracked, linked.Id, cancellationToken))
                {
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    var reused = await ResultAsync(tracked, hasCoordinates: true, cancellationToken, reusedCheckout: true);
                    await AuditAsync(tracked, trigger, reused);
                    return reused;
                }
            }

            // Automatic paths never redo a qualification that already produced
            // an AMID with evidence. Admin may re-run (the caller makes sure the
            // order isn't with Openserve yet).
            var evidenceCurrent = hasAmid && linked is { CallSucceeded: true, AddressIdentified: true }
                && string.Equals(linked.Amid, current.OpenserveAmId, StringComparison.OrdinalIgnoreCase);
            if (trigger != OpenserveQualificationTrigger.AdminManual && evidenceCurrent)
                return new OpenserveQualificationRunResult(OpenserveQualificationRunStatus.Skipped, "AMID and Product Qualification evidence already captured.", current.OpenserveAmId, current.OpenserveBuildingNumId);

            var settings = _configProvider.Current;
            if (!settings.Enabled) return Skipped("Openserve integration is disabled.");

            if (!ignoreCooldown && current.OpenserveQualifiedAtUtc is { } lastRun)
            {
                var cooldown = TimeSpan.FromMinutes(Math.Max(0, settings.SubmissionRecovery.QualificationCooldownMinutes));
                if (lastRun > DateTime.UtcNow - cooldown)
                    return Skipped($"Product Qualification already ran at {lastRun:yyyy-MM-dd HH:mm} UTC: {current.OpenserveQualificationFailureReason ?? (hasAmid ? "evidence not recorded" : "no AMID returned")}");
            }

            var order = await _dbContext.Orders.FirstAsync(o => o.Id == orderId, cancellationToken);
            var hasCoordinates = await HasUsableCoordinatesAsync(order, cancellationToken);
            await RunForOrderSafeAsync(order, OpenserveQualificationEvidence.PurposeOf(trigger), cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var result = await ResultAsync(order, hasCoordinates, cancellationToken);
            await AuditAsync(order, trigger, result);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Openserve][qualify] Unexpected error running Product Qualification for order {OrderId} ({Trigger}).", orderId, trigger);
            return new OpenserveQualificationRunResult(OpenserveQualificationRunStatus.NoAmid, "An unexpected error occurred during Openserve qualification.");
        }
    }

    public async Task<OpenserveQualificationRunResult> RefreshBuildingCandidatesAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        try
        {
            var order = await _dbContext.Orders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
            if (order is null) return new OpenserveQualificationRunResult(OpenserveQualificationRunStatus.NotFound, "Order not found.");
            if (order.PackageType != ServicePackageType.Fibre) return Skipped("Not a Fibre order — Product Qualification does not apply.");

            var amid = await _dbContext.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.OpenserveAmId).FirstOrDefaultAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(amid)) return Skipped("The order has no AMID yet — run Product Qualification first.");
            if (!_configProvider.Current.Enabled) return Skipped("Openserve integration is disabled.");

            var result = await _client.QualifyAsync(new OpenserveQualificationQuery { Amid = amid, BuildingInfo = true }, cancellationToken);
            var now = DateTime.UtcNow;
            var log = NewLog(result, now, result.IsSuccess, result.IsSuccess ? null : $"{result.ErrorCode}: {result.ErrorMessage} (building refresh, order {order.OrderNumber})");
            _dbContext.OpenserveIntegrationLogs.Add(log);
            await _dbContext.SaveChangesAsync(cancellationToken);

            OpenserveQualificationRunResult run;
            if (!result.IsSuccess || result.Outcome is null)
            {
                run = new OpenserveQualificationRunResult(OpenserveQualificationRunStatus.NoAmid, Truncate(result.ErrorMessage ?? "Qualification lookup failed.", 500)!);
            }
            else if (!string.IsNullOrWhiteSpace(result.Outcome.Amid) && !string.Equals(result.Outcome.Amid, amid, StringComparison.OrdinalIgnoreCase))
            {
                // Rows for a different AMID don't describe this order — keep nothing.
                run = new OpenserveQualificationRunResult(OpenserveQualificationRunStatus.NoAmid, $"Openserve answered for AMID {result.Outcome.Amid}, not the order's AMID {amid} — building/unit rows not stored.");
            }
            else
            {
                OpenserveBuildingCandidates.Apply(order, result.Outcome.Buildings, result.Outcome.BuildingMatchCount, result.Outcome.BuildingNumId);

                // The same response carries FTTH, products and the canonical
                // address for this AMID — record it as the order's evidence
                // (this is how an order qualified before evidence existed gets it).
                var evidence = OpenserveQualificationEvidence.Build(result, OpenserveQualificationPurpose.BuildingCandidatesRefresh, null, null, amid, log.Id, now);
                evidence.OrderId = order.Id;
                if (evidence.AddressIdentified)
                {
                    await EvaluateForOrderAsync(evidence, order, cancellationToken);
                    await CarryOverAddressAcceptanceAsync(order.OpenserveQualificationResultId, evidence, cancellationToken);
                    _dbContext.OpenserveQualificationResults.Add(evidence);
                    order.OpenserveQualificationResultId = evidence.Id;
                }
                await _dbContext.SaveChangesAsync(cancellationToken);
                run = new OpenserveQualificationRunResult(OpenserveQualificationRunStatus.Qualified,
                    $"{order.OpenserveBuildingCandidateCount} building/unit row(s) returned for AMID {amid}.", amid, order.OpenserveBuildingNumId);
            }

            await AuditAsync(order, OpenserveQualificationTrigger.BuildingCandidatesRefresh, run);
            return run;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Openserve][qualify] Unexpected error refreshing building/unit rows for order {OrderId}.", orderId);
            return new OpenserveQualificationRunResult(OpenserveQualificationRunStatus.NoAmid, "An unexpected error occurred during Openserve qualification.");
        }
    }

    public async Task<bool> HasUsableCoordinatesAsync(Order order, CancellationToken cancellationToken = default)
    {
        var (latitude, longitude) = await ResolveCoordinatesAsync(order, cancellationToken);
        return AreUsable(latitude, longitude);
    }

    public async Task<OpenserveLocationEligibility> EvaluateLocationAsync(OpenserveLocationQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_configProvider.Current.CanQualify) return OpenserveLocationEligibility.NotAuthoritative;
            if (!AreUsable(query.Latitude, query.Longitude))
            {
                return new OpenserveLocationEligibility
                {
                    Status = OpenserveLocationStatus.NoCoordinates,
                    CustomerTitle = "We need your exact location.",
                    CustomerMessage = "Choose your address from the suggestions so we can check Fibre at your property."
                };
            }

            var (evidence, persisted, evidenceId) = await ObtainLocationEvidenceAsync(query, OpenserveQualificationPurpose.CoverageCheck, persistCopyOnReuse: false, cancellationToken);
            OpenserveQualificationEvidence.Evaluate(evidence, query.Customer, null, null, null);
            if (persisted) await _dbContext.SaveChangesAsync(cancellationToken);

            if (!evidence.CallSucceeded)
            {
                return new OpenserveLocationEligibility
                {
                    Status = OpenserveLocationStatus.QualificationUnavailable,
                    EvidenceId = evidenceId,
                    Evidence = evidence,
                    CustomerTitle = "We couldn't confirm Fibre availability right now.",
                    CustomerMessage = "Please try again in a few minutes."
                };
            }

            var location = OpenserveFibreEligibility.Assess(evidence, null, null);
            var packages = (await ActiveFibrePackagesAsync(cancellationToken))
                .Select(p => new OpenservePackageEligibility(p.Id, OpenserveFibreEligibility.Assess(evidence, p.Mapping, p.DownloadSpeedMbps)))
                .ToList();
            var (title, message) = OpenserveFibreEligibility.LocationText(location, packages.Count(p => p.Assessment.IsEligible));

            return new OpenserveLocationEligibility
            {
                Status = OpenserveLocationStatus.Evaluated,
                EvidenceId = evidenceId,
                Evidence = evidence,
                Location = location,
                Packages = packages,
                CustomerTitle = title,
                CustomerMessage = message
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Openserve][qualify] Unexpected error evaluating a location.");
            return new OpenserveLocationEligibility
            {
                Status = OpenserveLocationStatus.QualificationUnavailable,
                CustomerTitle = "We couldn't confirm Fibre availability right now.",
                CustomerMessage = "Please try again in a few minutes."
            };
        }
    }

    public async Task<OpenserveCheckoutGateResult> CheckFibreCheckoutAsync(OpenserveLocationQuery query, Guid servicePackageId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_configProvider.Current.CanQualify) return OpenserveCheckoutGateResult.NotApplicable;

            var package = await _dbContext.ServicePackages.AsNoTracking().Where(p => p.Id == servicePackageId)
                .Select(p => new { p.Type, p.DownloadSpeedMbps }).FirstOrDefaultAsync(cancellationToken);
            if (package is null || package.Type != ServicePackageType.Fibre) return OpenserveCheckoutGateResult.NotApplicable;

            if (!AreUsable(query.Latitude, query.Longitude))
                return new OpenserveCheckoutGateResult(true, false, ErrorCodes.VALIDATION_ERROR, "Please confirm coverage for your installation address before placing an order.");

            var mapping = await _dbContext.PackageOpenserveMappings.AsNoTracking().FirstOrDefaultAsync(m => m.ServicePackageId == servicePackageId && m.IsEnabled, cancellationToken);
            var (evidence, _, _) = await ObtainLocationEvidenceAsync(query, OpenserveQualificationPurpose.CheckoutGate, persistCopyOnReuse: true, cancellationToken);
            var assessment = OpenserveQualificationEvidence.Evaluate(evidence, query.Customer, servicePackageId, mapping, package.DownloadSpeedMbps);
            await _dbContext.SaveChangesAsync(cancellationToken);

            if (!evidence.CallSucceeded)
                return new OpenserveCheckoutGateResult(true, false, ErrorCodes.UPSTREAM_UNAVAILABLE, QualificationUnavailableMessage, evidence.Id);
            if (assessment.IsEligible) return new OpenserveCheckoutGateResult(true, true, EvidenceId: evidence.Id);

            var (title, message) = assessment.CustomerText;
            _logger.LogInformation("[Openserve][checkout] Fibre package {PackageId} refused at checkout: {Code} (evidence {EvidenceId}).", servicePackageId,
                assessment.Blocker?.Code, evidence.Id);
            return new OpenserveCheckoutGateResult(true, false, ErrorCodes.FIBRE_NOT_ELIGIBLE, $"{title} {message}", evidence.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Fail closed: while qualification is the authority, an unverifiable
            // Fibre package is never let through to payment.
            _logger.LogError(ex, "[Openserve][checkout] Unexpected error checking Fibre package {PackageId}.", servicePackageId);
            return new OpenserveCheckoutGateResult(true, false, ErrorCodes.UPSTREAM_UNAVAILABLE, QualificationUnavailableMessage);
        }
    }

    /// <summary>Real coordinates only: both present, in range, and not the (0,0) placeholder.</summary>
    public static bool AreUsable(decimal? latitude, decimal? longitude) =>
        latitude is { } lat && longitude is { } lon && lat is >= -90m and <= 90m && lon is >= -180m and <= 180m && !(lat == 0m && lon == 0m);

    // ─── order runs ─────────────────────────────────────────────────

    private async Task RunForOrderSafeAsync(Order order, OpenserveQualificationPurpose purpose, CancellationToken cancellationToken)
    {
        try
        {
            if (!_configProvider.Current.Enabled)
            {
                // Disabled-by-design is not a failure — leave
                // OpenserveQualifiedAtUtc null so admin can distinguish
                // "never attempted" from "attempted and failed".
                _logger.LogDebug("[Openserve][qualify] Skipped for order {OrderNumber} — integration disabled.", order.OrderNumber);
                return;
            }

            var (latitude, longitude) = await ResolveCoordinatesAsync(order, cancellationToken);
            var now = DateTime.UtcNow;

            if (!AreUsable(latitude, longitude))
            {
                // Never call Openserve with missing or placeholder (0,0) coordinates.
                order.OpenserveQualifiedAtUtc = now;
                if (string.IsNullOrWhiteSpace(order.OpenserveAmId)) order.OpenserveQualificationFailureReason = MissingCoordinatesReason;
                _logger.LogWarning("[Openserve][qualify] Order {OrderNumber} has no usable coordinates; qualification skipped.", order.OrderNumber);
                return;
            }

            var result = await _client.QualifyAsync(new OpenserveQualificationQuery
            {
                Latitude = latitude!.Value,
                Longitude = longitude!.Value,
                BuildingInfo = true
            }, cancellationToken);

            var amidReturned = !string.IsNullOrWhiteSpace(result.Outcome?.Amid);
            var log = NewLog(result, now, result.IsSuccess && amidReturned, result.IsSuccess
                ? (amidReturned ? null : $"Qualified but no AMID returned for order {order.OrderNumber}.")
                : $"{result.ErrorCode}: {result.ErrorMessage} (order {order.OrderNumber})");
            _dbContext.OpenserveIntegrationLogs.Add(log);

            var evidence = OpenserveQualificationEvidence.Build(result, purpose, OpenserveQualificationEvidence.RoundCoordinate(latitude),
                OpenserveQualificationEvidence.RoundCoordinate(longitude), null, log.Id, now);
            evidence.OrderId = order.Id;
            await EvaluateForOrderAsync(evidence, order, cancellationToken);
            await CarryOverAddressAcceptanceAsync(order.OpenserveQualificationResultId, evidence, cancellationToken);
            _dbContext.OpenserveQualificationResults.Add(evidence);
            await _dbContext.SaveChangesAsync(cancellationToken);

            ApplyToOrder(order, evidence, result.IsSuccess ? null : result.ErrorMessage);

            _logger.LogInformation("[Openserve][qualify] Order {OrderNumber} qualified: AMID={Amid} fibre={Fibre} address={AddressMatch} product={Product} buildingNumId={BuildingNumId} buildingMatches={BuildingMatchCount}",
                order.OrderNumber, evidence.Amid, evidence.FibreAvailability, evidence.AddressMatch, evidence.ProductEligibility, order.OpenserveBuildingNumId, evidence.BuildingCandidateCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Openserve][qualify] Unexpected error qualifying order {OrderNumber}.", order.OrderNumber);
            order.OpenserveQualifiedAtUtc = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(order.OpenserveAmId)) order.OpenserveQualificationFailureReason = "An unexpected error occurred during Openserve qualification.";
        }
    }

    /// <summary>
    /// Copies a qualification's outcome onto the order. The AMID (address) is
    /// stored whenever Openserve identified the address; Fibre/product/address
    /// eligibility lives on the evidence the order now points to. A failed
    /// call never erases an AMID the order already has.
    /// </summary>
    private static void ApplyToOrder(Order order, OpenserveQualificationResult evidence, string? callError)
    {
        if (!evidence.CallSucceeded && !string.IsNullOrWhiteSpace(order.OpenserveAmId))
            return; // a transient failure doesn't undo earlier evidence

        var previousAmid = order.OpenserveAmId;
        var previousBuilding = order.OpenserveBuildingNumId;
        order.OpenserveQualifiedAtUtc = evidence.QualifiedAtUtc;
        order.OpenserveQualificationResultId = evidence.Id;

        if (evidence.CallSucceeded && evidence.AddressIdentified)
        {
            order.OpenserveAmId = evidence.Amid;

            // MDU: store every buildingInfo row Openserve returned and pick the
            // customer's own row only when that's deterministic. Several rows
            // and no match → building/unit unresolved and submission blocked
            // until Admin chooses (never guessed).
            var rows = OpenserveBuildingCandidates.Read(evidence.BuildingCandidatesJson);
            OpenserveBuildingCandidates.Apply(order, rows, evidence.BuildingCandidateCount, rows.Count == 1 ? rows[0].BldNumId : null);

            // A re-run for the same AMID keeps the building/unit an Admin already chose, if Openserve still returns that row.
            if (string.Equals(previousAmid, evidence.Amid, StringComparison.OrdinalIgnoreCase) && previousBuilding is not null && order.OpenserveBuildingNumId is null
                && rows.FirstOrDefault(r => string.Equals(r.BldNumId, previousBuilding, StringComparison.Ordinal)) is { } kept)
            {
                order.OpenserveBuildingNumId = kept.BldNumId;
                order.OpenserveBuildingName = kept.BuildingName;
                order.OpenserveFloor = kept.Floor;
                order.OpenserveUnit = kept.Num;
                order.OpenserveQualificationFailureReason = null;
            }
            return;
        }

        order.OpenserveAmId = null;
        order.OpenserveBuildingNumId = null;
        order.OpenserveBuildingName = null;
        order.OpenserveFloor = null;
        order.OpenserveUnit = null;
        order.OpenserveBuildingCandidateCount = null;
        order.OpenserveBuildingCandidatesJson = null;
        order.OpenserveQualificationFailureReason = evidence.CallSucceeded
            ? "Openserve returned no AMID for this address."
            : Truncate(callError ?? evidence.ErrorMessage ?? "Qualification lookup failed.", 500);
    }

    /// <summary>Uses the evidence the checkout gate recorded — only when it was taken for this order's own coordinates and isn't another order's.</summary>
    private async Task<bool> TryApplyCheckoutEvidenceAsync(Order order, Guid evidenceId, CancellationToken cancellationToken)
    {
        var evidence = await _dbContext.OpenserveQualificationResults.Include(r => r.Products).FirstOrDefaultAsync(r => r.Id == evidenceId, cancellationToken);
        if (evidence is null || !evidence.CallSucceeded) return false;
        if (evidence.OrderId is { } owner && owner != order.Id) return false;

        var (latitude, longitude) = await ResolveCoordinatesAsync(order, cancellationToken);
        if (!OpenserveQualificationEvidence.SameCoordinates(evidence.QueryLatitude, evidence.QueryLongitude, latitude, longitude)) return false;

        evidence.OrderId = order.Id;
        await EvaluateForOrderAsync(evidence, order, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        ApplyToOrder(order, evidence, null);
        _logger.LogInformation("[Openserve][qualify] Order {OrderNumber} uses checkout qualification {EvidenceId}: AMID={Amid} fibre={Fibre} product={Product}",
            order.OrderNumber, evidence.Id, evidence.Amid, evidence.FibreAvailability, evidence.ProductEligibility);
        return true;
    }

    private async Task EvaluateForOrderAsync(OpenserveQualificationResult evidence, Order order, CancellationToken cancellationToken)
    {
        PackageOpenserveMapping? mapping = null;
        int? download = null;
        if (order.ServicePackageId is { } packageId)
        {
            mapping = await _dbContext.PackageOpenserveMappings.AsNoTracking().FirstOrDefaultAsync(m => m.ServicePackageId == packageId && m.IsEnabled, cancellationToken);
            download = await _dbContext.ServicePackages.AsNoTracking().Where(p => p.Id == packageId).Select(p => p.DownloadSpeedMbps).FirstOrDefaultAsync(cancellationToken);
        }
        OpenserveQualificationEvidence.Evaluate(evidence, OpenserveQualificationEvidence.CustomerAddressOf(order), order.ServicePackageId, mapping, download);
    }

    /// <summary>An Admin's acceptance of Openserve's address stays valid when a re-run returns the same AMID.</summary>
    private async Task CarryOverAddressAcceptanceAsync(Guid? previousEvidenceId, OpenserveQualificationResult evidence, CancellationToken cancellationToken)
    {
        if (previousEvidenceId is not { } previousId || !evidence.AddressIdentified) return;
        var previous = await _dbContext.OpenserveQualificationResults.AsNoTracking().Where(r => r.Id == previousId)
            .Select(r => new { r.Amid, r.AddressAcceptedAtUtc, r.AddressAcceptedByUserId, r.AddressAcceptanceNote }).FirstOrDefaultAsync(cancellationToken);
        if (previous?.AddressAcceptedAtUtc is null || !string.Equals(previous.Amid, evidence.Amid, StringComparison.OrdinalIgnoreCase)) return;
        evidence.AddressAcceptedAtUtc = previous.AddressAcceptedAtUtc;
        evidence.AddressAcceptedByUserId = previous.AddressAcceptedByUserId;
        evidence.AddressAcceptanceNote = previous.AddressAcceptanceNote;
    }

    private async Task<OpenserveQualificationRunResult> ResultAsync(Order order, bool hasCoordinates, CancellationToken cancellationToken, bool reusedCheckout = false)
    {
        if (!hasCoordinates) return new OpenserveQualificationRunResult(OpenserveQualificationRunStatus.NoCoordinates, MissingCoordinatesReason);
        if (string.IsNullOrWhiteSpace(order.OpenserveAmId))
            return new OpenserveQualificationRunResult(OpenserveQualificationRunStatus.NoAmid, order.OpenserveQualificationFailureReason ?? "Openserve returned no AMID for this address.");

        var evidence = order.OpenserveQualificationResultId is { } id
            ? await _dbContext.OpenserveQualificationResults.AsNoTracking().Include(r => r.Products).FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            : null;
        var mapping = order.ServicePackageId is { } packageId
            ? await _dbContext.PackageOpenserveMappings.AsNoTracking().FirstOrDefaultAsync(m => m.ServicePackageId == packageId && m.IsEnabled, cancellationToken)
            : null;
        var download = order.ServicePackageId is { } pid
            ? await _dbContext.ServicePackages.AsNoTracking().Where(p => p.Id == pid).Select(p => p.DownloadSpeedMbps).FirstOrDefaultAsync(cancellationToken)
            : null;
        var assessment = OpenserveFibreEligibility.Assess(evidence, mapping, download);
        var source = reusedCheckout ? " (from the checkout qualification)" : string.Empty;
        var message = assessment.IsEligible
            ? $"AMID {order.OpenserveAmId} captured{source} — Fibre available: {assessment.ProductReason}"
            : $"AMID {order.OpenserveAmId} captured{source} (address identified), but the order can't be submitted: {assessment.Blocker?.Reason ?? "not eligible."}";
        return new OpenserveQualificationRunResult(OpenserveQualificationRunStatus.Qualified, message, order.OpenserveAmId, order.OpenserveBuildingNumId, assessment.IsEligible);
    }

    // ─── location (pre-order) runs ──────────────────────────────────

    /// <summary>
    /// Evidence for a location: a successful call for the exact same
    /// coordinates within the reuse window is reused (copied when it must be
    /// persisted for this purpose); otherwise Openserve is called and the
    /// call is logged and recorded.
    /// </summary>
    private async Task<(OpenserveQualificationResult Evidence, bool Persisted, Guid EvidenceId)> ObtainLocationEvidenceAsync(OpenserveLocationQuery query, OpenserveQualificationPurpose purpose,
        bool persistCopyOnReuse, CancellationToken cancellationToken)
    {
        var settings = _configProvider.Current;
        var latitude = OpenserveQualificationEvidence.RoundCoordinate(query.Latitude);
        var longitude = OpenserveQualificationEvidence.RoundCoordinate(query.Longitude);
        var now = DateTime.UtcNow;

        if (settings.Qualification.ReuseMinutes > 0)
        {
            var cutoff = now.AddMinutes(-settings.Qualification.ReuseMinutes);
            var recent = await _dbContext.OpenserveQualificationResults.AsNoTracking()
                .Where(r => r.QualifiedAtUtc >= cutoff && r.CallSucceeded && r.QueryLatitude != null && r.QueryLongitude != null)
                .OrderByDescending(r => r.QualifiedAtUtc)
                .Take(MaxReuseCandidates)
                .Select(r => new { r.Id, r.QueryLatitude, r.QueryLongitude })
                .ToListAsync(cancellationToken);
            var hit = recent.FirstOrDefault(r => OpenserveQualificationEvidence.SameCoordinates(r.QueryLatitude, r.QueryLongitude, latitude, longitude));
            if (hit is not null)
            {
                var source = await _dbContext.OpenserveQualificationResults.AsNoTracking().Include(r => r.Products).FirstAsync(r => r.Id == hit.Id, cancellationToken);
                var copy = OpenserveQualificationEvidence.CopyFacts(source, purpose, now);
                if (persistCopyOnReuse) _dbContext.OpenserveQualificationResults.Add(copy);
                return (copy, persistCopyOnReuse, persistCopyOnReuse ? copy.Id : source.Id);
            }
        }

        var result = await _client.QualifyAsync(new OpenserveQualificationQuery { Latitude = latitude, Longitude = longitude, BuildingInfo = true }, cancellationToken);
        var context = purpose == OpenserveQualificationPurpose.CheckoutGate ? "checkout check" : "coverage check";
        var log = NewLog(result, now, result.IsSuccess, result.IsSuccess ? null : $"{result.ErrorCode}: {result.ErrorMessage} ({context})");
        _dbContext.OpenserveIntegrationLogs.Add(log);
        var evidence = OpenserveQualificationEvidence.Build(result, purpose, latitude, longitude, null, log.Id, now);
        _dbContext.OpenserveQualificationResults.Add(evidence);
        return (evidence, true, evidence.Id);
    }

    private async Task<IReadOnlyList<(Guid Id, int? DownloadSpeedMbps, PackageOpenserveMapping? Mapping)>> ActiveFibrePackagesAsync(CancellationToken cancellationToken)
    {
        var packages = await _dbContext.ServicePackages.AsNoTracking()
            .Where(p => p.Type == ServicePackageType.Fibre && p.Status == ServicePackageStatus.Active)
            .Select(p => new { p.Id, p.DownloadSpeedMbps })
            .ToListAsync(cancellationToken);
        var ids = packages.Select(p => p.Id).ToList();
        var mappings = await _dbContext.PackageOpenserveMappings.AsNoTracking()
            .Where(m => ids.Contains(m.ServicePackageId) && m.IsEnabled)
            .ToListAsync(cancellationToken);
        return packages.Select(p => (p.Id, p.DownloadSpeedMbps, mappings.FirstOrDefault(m => m.ServicePackageId == p.Id))).ToList();
    }

    // ─── helpers ────────────────────────────────────────────────────

    private static OpenserveIntegrationLog NewLog(OpenserveApiCallResult<OpenserveQualificationOutcome> result, DateTime now, bool isSuccess, string? errorSummary) => new()
    {
        Id = Guid.NewGuid(),
        OpenserveOrderId = null, // no OpenserveOrder exists at qualification time
        Direction = OpenserveIntegrationDirection.Outbound,
        OperationType = OpenserveOperationType.ProductQualification,
        MessageId = result.MessageId,
        HttpMethod = result.HttpMethod,
        Endpoint = result.Endpoint,
        RequestHeadersJson = result.RequestHeadersJson,
        ResponseStatusCode = result.HttpStatusCode,
        ResponseBodyJson = Truncate(result.ResponseBodyJson, 50_000),
        OccurredAtUtc = now,
        IsSuccess = isSuccess,
        ErrorSummary = Truncate(errorSummary, 500)
    };

    // Order.Latitude/Longitude come straight off the request DTO
    // (Google Places autocomplete result) and are set for the common
    // path. A customer order can ALSO prove coverage purely via a
    // confirmed CoverageRequestId without Latitude/Longitude being set
    // directly on the Order — fall back to the linked CoverageRequest's
    // coordinates in that case rather than skipping qualification.
    private async Task<(decimal? Latitude, decimal? Longitude)> ResolveCoordinatesAsync(Order order, CancellationToken cancellationToken)
    {
        if (order.Latitude.HasValue && order.Longitude.HasValue)
            return (order.Latitude, order.Longitude);

        if (!order.CoverageRequestId.HasValue) return (null, null);

        var coverageRequest = await _dbContext.CoverageRequests
            .AsNoTracking()
            .Where(c => c.Id == order.CoverageRequestId.Value)
            .Select(c => new { c.Latitude, c.Longitude })
            .FirstOrDefaultAsync(cancellationToken);

        return coverageRequest is null ? (null, null) : (coverageRequest.Latitude, coverageRequest.Longitude);
    }

    private async Task AuditAsync(Order order, OpenserveQualificationTrigger trigger, OpenserveQualificationRunResult result)
    {
        if (_auditService is null) return;
        try
        {
            var success = result.Status == OpenserveQualificationRunStatus.Qualified;
            var summary = success
                ? $"Openserve Product Qualification ({trigger}) for order {order.OrderNumber}: AMID {result.AmId} captured — {(result.FibreEligible == true ? "eligible" : "not eligible")}."
                : $"Openserve Product Qualification ({trigger}) for order {order.OrderNumber} did not return an AMID: {result.Message}";
            var evidence = order.OpenserveQualificationResultId is { } id
                ? await _dbContext.OpenserveQualificationResults.AsNoTracking().Where(r => r.Id == id)
                    .Select(r => new { r.FibreAvailability, r.AddressMatch, r.ProductEligibility, r.CanonicalAddress }).FirstOrDefaultAsync()
                : null;
            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser?.UserId,
                ActorType = _currentUser?.UserId is not null ? AuditActorType.Admin : AuditActorType.System,
                ActionType = AuditActionType.OpenserveOrderQualificationRun,
                EntityType = AuditEntityType.Order,
                EntityId = order.Id,
                EntityName = order.OrderNumber,
                Summary = Truncate(summary, 1000)!,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    trigger = trigger.ToString(),
                    status = result.Status.ToString(),
                    amid = order.OpenserveAmId,
                    buildingNumId = order.OpenserveBuildingNumId,
                    buildingCandidates = order.OpenserveBuildingCandidateCount,
                    reason = order.OpenserveQualificationFailureReason,
                    eligible = result.FibreEligible,
                    evidenceId = order.OpenserveQualificationResultId,
                    fibre = evidence?.FibreAvailability.ToString(),
                    addressMatch = evidence?.AddressMatch.ToString(),
                    productEligibility = evidence?.ProductEligibility.ToString(),
                    openserveAddress = evidence?.CanonicalAddress
                }),
                IpAddress = _currentUser?.IpAddress,
                UserAgent = _currentUser?.UserAgent,
                IsSuccess = success
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Openserve][qualify] Audit write failed for order {OrderNumber}.", order.OrderNumber);
        }
    }

    private static OpenserveQualificationRunResult Skipped(string message) => new(OpenserveQualificationRunStatus.Skipped, message);

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max];
    }
}

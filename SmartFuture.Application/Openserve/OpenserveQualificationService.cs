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

    /// <summary>Customer wording when Openserve lists several units at the address and the customer's unit didn't match one.</summary>
    public const string ServicePremisesChoiceExpiredMessage =
        "The Openserve service location you chose earlier can't be used any more (the choice expired or no longer matches your address). Please check coverage and choose your service location again.";

    public const string BuildingUnitRequiredMessage =
        "Openserve lists several units at this address and we couldn't match yours. Enter your unit/flat number and building name exactly as they appear at the property, or request a coverage check.";

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
        await RunForOrderSafeAsync(order, OpenserveQualificationPurpose.OrderCreated, allowReuse: true, cancellationToken);

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

            // Payment-first conversion: the checkout gate already verified and
            // qualified this exact address — apply that evidence instead of calling again.
            if (!hasAmid && trigger != OpenserveQualificationTrigger.AdminManual && linked is { CallSucceeded: true, AddressIdentified: true } && (linked.OrderId is null || linked.OrderId == orderId))
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
                    return Skipped($"Product Qualification already ran at {lastRun:yyyy-MM-dd HH:mm} UTC: {current.OpenserveQualificationFailureReason ?? (hasAmid ? "evidence not recorded" : "no AMID established")}");
            }

            var order = await _dbContext.Orders.FirstAsync(o => o.Id == orderId, cancellationToken);
            var previousAmid = order.OpenserveAmId;
            var hasCoordinates = await HasUsableCoordinatesAsync(order, cancellationToken);
            // Admin asks for a fresh answer; automatic paths may reuse a very recent one.
            await RunForOrderSafeAsync(order, OpenserveQualificationEvidence.PurposeOf(trigger), allowReuse: trigger != OpenserveQualificationTrigger.AdminManual, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var result = await ResultAsync(order, hasCoordinates, cancellationToken);
            await AuditAsync(order, trigger, result, previousAmid);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Openserve][qualify] Unexpected error running Product Qualification for order {OrderId} ({Trigger}).", orderId, trigger);
            return new OpenserveQualificationRunResult(OpenserveQualificationRunStatus.NoAmid, "An unexpected error occurred during Openserve qualification.");
        }
    }

    public async Task<OpenserveQualificationRunResult> SelectAddressCandidateAsync(Guid orderId, string amid, string note, CancellationToken cancellationToken = default)
    {
        try
        {
            var order = await _dbContext.Orders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
            if (order is null) return new OpenserveQualificationRunResult(OpenserveQualificationRunStatus.NotFound, "Order not found.");
            if (order.PackageType != ServicePackageType.Fibre) return Skipped("Not a Fibre order — Openserve address verification does not apply.");
            if (!_configProvider.Current.Enabled) return Skipped("Openserve integration is disabled.");

            var verification = order.OpenserveQualificationResultId is { } linkedId
                ? await _dbContext.OpenserveQualificationResults.AsNoTracking().FirstOrDefaultAsync(r => r.Id == linkedId, cancellationToken)
                : null;
            var wanted = amid?.Trim();
            var chosen = OpenserveAddressCandidateMatcher.Read(verification?.AddressCandidatesJson)
                .FirstOrDefault(c => !string.IsNullOrWhiteSpace(wanted) && string.Equals(c.Amid?.Trim(), wanted, StringComparison.Ordinal));
            // Only an AMID Openserve itself returned for this location — never a typed or guessed one.
            if (verification is null || chosen is null)
                return new OpenserveQualificationRunResult(OpenserveQualificationRunStatus.Skipped, "That AMID is not one of the Openserve address records returned for this order's location. Re-run address verification first.");

            var previousAmid = order.OpenserveAmId;
            var now = DateTime.UtcNow;
            var evidence = await QualifyAmidAsync(chosen.Amid!, OpenserveQualificationPurpose.AdminCandidateSelection, $"Admin address choice, order {order.OrderNumber}", allowReuse: false,
                cancellationToken);
            evidence.QueryLatitude = verification.QueryLatitude;
            evidence.QueryLongitude = verification.QueryLongitude;
            OpenserveQualificationEvidence.CopyVerification(verification, evidence);
            evidence.AddressResolution = OpenserveAddressResolution.AdminSelected;
            evidence.AddressResolvedByUserId = _currentUser?.UserId;
            evidence.AddressResolvedAtUtc = now;
            evidence.AddressResolutionNote = Truncate(note?.Trim(), 500);
            evidence.AddressResolutionDetail = Truncate($"Admin chose {chosen.Address} (AMID {chosen.Amid}, {OpenserveAddressMatcher.Num(chosen.DistanceMeters)} m from the customer's location) as the customer's premises. "
                + $"Automatic match verdict: {chosen.Match} — {chosen.MatchDetail}", 1000);
            evidence.OrderId = order.Id;
            await EvaluateForOrderAsync(evidence, order, cancellationToken);
            if (evidence.AddressIdentified && evidence.AddressMatch != OpenserveAddressMatch.Matched)
            {
                // The Admin explicitly confirmed this premises — that IS the acceptance of the difference.
                evidence.AddressAcceptedAtUtc = now;
                evidence.AddressAcceptedByUserId = _currentUser?.UserId;
                evidence.AddressAcceptanceNote = evidence.AddressResolutionNote;
            }
            _dbContext.OpenserveQualificationResults.Add(evidence);
            await _dbContext.SaveChangesAsync(cancellationToken);
            ApplyToOrder(order, evidence, evidence.ErrorMessage);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var result = await ResultAsync(order, hasCoordinates: true, cancellationToken);
            await AuditCandidateSelectionAsync(order, chosen, evidence, previousAmid, result);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Openserve][verify] Unexpected error applying an Admin address choice for order {OrderId}.", orderId);
            return new OpenserveQualificationRunResult(OpenserveQualificationRunStatus.NoAmid, "An unexpected error occurred while qualifying the chosen Openserve address.");
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
                    var previous = await PreviousEvidenceAsync(order.OpenserveQualificationResultId, cancellationToken);
                    if (previous is not null && string.Equals(previous.Amid, amid, StringComparison.OrdinalIgnoreCase))
                    {
                        // Same premises — keep how it was established (verification candidates, Admin choice).
                        OpenserveQualificationEvidence.CopyVerification(previous, evidence);
                        evidence.QueryLatitude = previous.QueryLatitude;
                        evidence.QueryLongitude = previous.QueryLongitude;
                    }
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

            var evidence = await ResolvePremisesAsync(query, OpenserveQualificationPurpose.CoverageCheck, "coverage check", allowReuse: true, cancellationToken);
            OpenserveQualificationEvidence.Evaluate(evidence, query.Customer, null, null, null);
            _dbContext.OpenserveQualificationResults.Add(evidence);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return await LocationResultAsync(evidence, cancellationToken);
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

    public async Task<OpenserveLocationEligibility> SelectServicePremisesAsync(OpenserveServicePremisesChoice choice, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_configProvider.Current.CanQualify) return OpenserveLocationEligibility.NotAuthoritative;
            if (!choice.CustomerConfirmed)
                return SelectionRejected("Please confirm your service location.", "Confirm that the Openserve service location you chose corresponds to your property.");

            var verification = await _dbContext.OpenserveQualificationResults.AsNoTracking().FirstOrDefaultAsync(r => r.Id == choice.VerificationReference, cancellationToken);
            if (VerificationProblem(verification) is { } problem) return SelectionRejected(problem.Title, problem.Message);

            var candidates = OpenserveAddressCandidateMatcher.Read(verification!.AddressCandidatesJson);
            // Only a record Openserve itself listed for this location — never a typed or guessed AMID.
            if (choice.CandidateIndex < 0 || choice.CandidateIndex >= candidates.Count || string.IsNullOrWhiteSpace(candidates[choice.CandidateIndex].Amid))
                return SelectionRejected("Please choose your service location again.", "That service location isn't one of the Openserve records listed for your address.");
            var chosen = candidates[choice.CandidateIndex];

            var evidence = await QualifyAmidAsync(chosen.Amid!, OpenserveQualificationPurpose.CustomerPremisesSelection, "customer service-location choice", allowReuse: true, cancellationToken);
            OpenserveQualificationEvidence.ApplyCustomerSelection(verification, evidence, chosen, _currentUser?.UserId, DateTime.UtcNow);
            var customer = string.IsNullOrWhiteSpace(choice.Customer.AddressLine1) ? new OpenserveAddressMatcher.CustomerAddress(verification.CustomerAddress, null, null, null) : choice.Customer;
            OpenserveQualificationEvidence.Evaluate(evidence, customer, null, null, null);
            _dbContext.OpenserveQualificationResults.Add(evidence);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var result = await LocationResultAsync(evidence, cancellationToken);
            await AuditServicePremisesSelectionAsync(verification, chosen, evidence, result);
            _logger.LogInformation("[Openserve][verify] Customer chose Openserve service location {Address} (AMID {Amid}) from verification {VerificationId}: {Status}, fibre {Fibre}.",
                chosen.Address, chosen.Amid, verification.Id, result.Status, evidence.FibreAvailability);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Openserve][verify] Unexpected error applying a customer's service-location choice.");
            return new OpenserveLocationEligibility
            {
                Status = OpenserveLocationStatus.QualificationUnavailable,
                CustomerTitle = "We couldn't confirm Fibre availability right now.",
                CustomerMessage = "Please try again in a few minutes."
            };
        }
    }

    /// <summary>Why a verification can't be chosen from by a customer (null = it can): it must be recent, customer-facing (no order yet) and unresolved.</summary>
    private (string Title, string Message)? VerificationProblem(OpenserveQualificationResult? verification)
    {
        var customerFacing = verification is { OrderId: null, CallSucceeded: true }
            && verification.Purpose is OpenserveQualificationPurpose.CoverageCheck or OpenserveQualificationPurpose.CheckoutGate or OpenserveQualificationPurpose.CustomerPremisesSelection;
        if (!customerFacing) return ("Please check coverage again.", "We couldn't find the address check for this choice.");
        if (verification!.AddressResolution == OpenserveAddressResolution.AutoMatched)
            return ("No service location needs to be chosen.", "Your address was matched to Openserve's records automatically.");
        if (verification.AddressResolution is not (OpenserveAddressResolution.Unresolved or OpenserveAddressResolution.CustomerSelected) || verification.AddressCandidateCount == 0)
            return ("Please check coverage again.", "Openserve listed no service locations to choose from for this address check.");
        if (verification.AddressVerifiedAtUtc is not { } verifiedAt || verifiedAt < DateTime.UtcNow.AddMinutes(-Math.Max(1, _configProvider.Current.Qualification.CustomerSelectionMinutes)))
            return ("Your address check has expired.", "Please check coverage again to see the Openserve service locations for your address.");
        return null;
    }

    private static OpenserveLocationEligibility SelectionRejected(string title, string message) =>
        new() { Status = OpenserveLocationStatus.SelectionRejected, CustomerTitle = title, CustomerMessage = message };

    /// <summary>The location-level verdict for a recorded evidence row: Fibre, address and every active Fibre package (only once the premises is established).</summary>
    private async Task<OpenserveLocationEligibility> LocationResultAsync(OpenserveQualificationResult evidence, CancellationToken cancellationToken)
    {
        var candidates = OpenserveAddressCandidateMatcher.Read(evidence.AddressCandidatesJson);
        if (!evidence.CallSucceeded)
        {
            return new OpenserveLocationEligibility
            {
                Status = OpenserveLocationStatus.QualificationUnavailable,
                EvidenceId = evidence.Id,
                Evidence = evidence,
                AddressCandidates = candidates,
                CustomerTitle = "We couldn't confirm Fibre availability right now.",
                CustomerMessage = "Please try again in a few minutes."
            };
        }

        var location = OpenserveFibreEligibility.Assess(evidence, null, null);
        var packages = location.PremisesEstablished
            ? (await ActiveFibrePackagesAsync(cancellationToken)).Select(p => new OpenservePackageEligibility(p.Id, OpenserveFibreEligibility.Assess(evidence, p.Mapping, p.DownloadSpeedMbps))).ToList()
            : new List<OpenservePackageEligibility>();
        var (title, message) = OpenserveFibreEligibility.LocationText(location, packages.Count(p => p.Assessment.IsEligible));

        return new OpenserveLocationEligibility
        {
            Status = OpenserveLocationStatus.Evaluated,
            EvidenceId = evidence.Id,
            Evidence = evidence,
            Location = location,
            Packages = packages,
            AddressCandidates = candidates,
            CustomerTitle = title,
            CustomerMessage = message
        };
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
            var evidence = await ResolvePremisesAsync(query, OpenserveQualificationPurpose.CheckoutGate, "checkout check", allowReuse: true, cancellationToken);
            var assessment = OpenserveQualificationEvidence.Evaluate(evidence, query.Customer, servicePackageId, mapping, package.DownloadSpeedMbps);
            _dbContext.OpenserveQualificationResults.Add(evidence);
            await _dbContext.SaveChangesAsync(cancellationToken);

            if (!evidence.CallSucceeded)
                return new OpenserveCheckoutGateResult(true, false, ErrorCodes.UPSTREAM_UNAVAILABLE, QualificationUnavailableMessage, evidence.Id, OpenserveFibreQualificationStatus.QualificationFailed);

            // MDU: several Openserve units and the customer's unit doesn't pick one → not orderable yet.
            var rows = OpenserveBuildingCandidates.Read(evidence.BuildingCandidatesJson);
            var buildingResolved = rows.Count <= 1 || OpenserveBuildingMatcher.Match(rows, query.UnitNumber, query.BuildingComplexName) is not null;
            var status = assessment.Status(buildingResolved);
            if (status == OpenserveFibreQualificationStatus.Orderable) return new OpenserveCheckoutGateResult(true, true, EvidenceId: evidence.Id, Status: status);

            var message = status == OpenserveFibreQualificationStatus.BuildingUnitRequired ? BuildingUnitRequiredMessage
                : status == OpenserveFibreQualificationStatus.AddressUnresolved && query.ServicePremisesReference is not null ? ServicePremisesChoiceExpiredMessage
                : $"{assessment.CustomerText.Title} {assessment.CustomerText.Message}";
            _logger.LogInformation("[Openserve][checkout] Fibre package {PackageId} refused at checkout: {Status} (evidence {EvidenceId}).", servicePackageId, status, evidence.Id);
            return new OpenserveCheckoutGateResult(true, false, OpenserveEligibilityAssessment.CheckoutErrorCode(status), message, evidence.Id, status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Fail closed: while qualification is the authority, an unverifiable
            // Fibre package is never let through to payment.
            _logger.LogError(ex, "[Openserve][checkout] Unexpected error checking Fibre package {PackageId}.", servicePackageId);
            return new OpenserveCheckoutGateResult(true, false, ErrorCodes.UPSTREAM_UNAVAILABLE, QualificationUnavailableMessage, Status: OpenserveFibreQualificationStatus.QualificationFailed);
        }
    }

    /// <summary>Real coordinates only: both present, in range, and not the (0,0) placeholder.</summary>
    public static bool AreUsable(decimal? latitude, decimal? longitude) =>
        latitude is { } lat && longitude is { } lon && lat is >= -90m and <= 90m && lon is >= -180m and <= 180m && !(lat == 0m && lon == 0m);

    // ─── premises resolution: FORCEVERIFY → match → qualify by AMID ──

    /// <summary>
    /// The address-resolution algorithm, shared by every path:
    ///   1. FORCEVERIFY=Y at the customer's coordinates → AddressVerify[] (nothing chosen yet);
    ///   2. match the customer's own address (street number + street; locality
    ///      must not contradict) against every candidate — distance never decides;
    ///   3. exactly one match → qualify THAT AMID (?AMID=…&amp;BuildingInfo=Y), whose
    ///      answer decides FTTH, products, speeds and building/unit;
    ///   4. no match / several → no AMID; Fibre and products NOT evaluated.
    /// The returned row is not yet added to the context.
    /// </summary>
    private async Task<OpenserveQualificationResult> ResolvePremisesAsync(OpenserveLocationQuery query, OpenserveQualificationPurpose purpose, string context, bool allowReuse,
        CancellationToken cancellationToken)
    {
        var latitude = OpenserveQualificationEvidence.RoundCoordinate(query.Latitude);
        var longitude = OpenserveQualificationEvidence.RoundCoordinate(query.Longitude);
        var verification = await VerifyAddressAsync(latitude!.Value, longitude!.Value, context, allowReuse, cancellationToken);
        var now = DateTime.UtcNow;

        if (verification.FailedCall is { } failedCall)
        {
            var failed = OpenserveQualificationEvidence.Build(failedCall, purpose, latitude, longitude, null, verification.LogId, now);
            failed.CallSucceeded = false;
            failed.ErrorMessage ??= "Openserve's address verification answer had no AddressVerify list.";
            failed.AddressVerifyIntegrationLogId = verification.LogId;
            failed.AddressResolutionDetail = Truncate($"Openserve address verification failed: {failed.ErrorMessage}", 1000);
            return failed;
        }

        var resolution = OpenserveAddressCandidateMatcher.Resolve(query.Customer, verification.Candidates);
        // An automatic match always wins; a customer's confirmed choice applies only while nothing matches.
        var customerChoice = resolution.Selected is null && query.ServicePremisesReference is { } reference
            ? await CustomerChoiceAsync(reference, latitude.Value, longitude.Value, verification.Candidates, cancellationToken)
            : null;
        OpenserveQualificationResult evidence;
        if (resolution.Selected is { } selected)
        {
            evidence = await QualifyAmidAsync(selected.Amid!, purpose, context, allowReuse, cancellationToken);
            evidence.QueryLatitude = latitude;
            evidence.QueryLongitude = longitude;
        }
        else if (customerChoice is not null)
        {
            evidence = await QualifyAmidAsync(customerChoice.Amid!, purpose, context, allowReuse, cancellationToken);
            evidence.QueryLatitude = latitude;
            evidence.QueryLongitude = longitude;
        }
        else
        {
            evidence = OpenserveQualificationEvidence.UnresolvedPremises(purpose, latitude, longitude, now);
        }

        OpenserveQualificationEvidence.ApplyVerification(evidence, resolution, verification.LogId, verification.VerifiedAtUtc);
        if (customerChoice is not null) OpenserveQualificationEvidence.CarryCustomerSelection(customerChoice, evidence);
        _logger.LogInformation("[Openserve][verify] {Context}: {Count} Openserve address candidate(s), resolution {Resolution}{Amid}.", context, resolution.Assessments.Count,
            resolution.Resolution, resolution.Selected is null ? string.Empty : $" → AMID {resolution.Selected.Amid}");
        return evidence;
    }

    /// <summary>
    /// The customer's earlier confirmed choice of service location, when it still applies: recent, made for these
    /// coordinates, not yet used by another order, and its record is still among the candidates Openserve lists now.
    /// </summary>
    private async Task<OpenserveQualificationResult?> CustomerChoiceAsync(Guid reference, decimal latitude, decimal longitude, IReadOnlyList<OpenserveAddressCandidate> currentCandidates,
        CancellationToken cancellationToken)
    {
        var choice = await _dbContext.OpenserveQualificationResults.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reference, cancellationToken);
        var cutoff = DateTime.UtcNow.AddMinutes(-Math.Max(1, _configProvider.Current.Qualification.CustomerSelectionMinutes));
        var usable = choice is { AddressResolution: OpenserveAddressResolution.CustomerSelected, OrderId: null, CallSucceeded: true, AddressIdentified: true, Amid: not null }
            && choice.AddressResolvedAtUtc >= cutoff
            && OpenserveQualificationEvidence.SameCoordinates(choice.QueryLatitude, choice.QueryLongitude, latitude, longitude)
            && currentCandidates.Any(c => string.Equals(c.Amid, choice.Amid, StringComparison.Ordinal));
        if (!usable) _logger.LogInformation("[Openserve][verify] Customer service-location choice {Reference} not applied (expired, other coordinates or no longer listed).", reference);
        return usable ? choice : null;
    }

    private sealed record AddressVerification(IReadOnlyList<OpenserveAddressCandidate> Candidates, Guid? LogId, DateTime VerifiedAtUtc,
        OpenserveApiCallResult<OpenserveQualificationOutcome>? FailedCall);

    /// <summary>
    /// AddressVerify[] for a point: a successful verification of the exact same
    /// coordinates within the reuse window is reused; otherwise Openserve is
    /// called with LAT/LON/BuildingInfo=Y/FORCEVERIFY=Y and the call is logged.
    /// </summary>
    private async Task<AddressVerification> VerifyAddressAsync(decimal latitude, decimal longitude, string context, bool allowReuse, CancellationToken cancellationToken)
    {
        var settings = _configProvider.Current;
        if (allowReuse && settings.Qualification.ReuseMinutes > 0)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-settings.Qualification.ReuseMinutes);
            var recent = await _dbContext.OpenserveQualificationResults.AsNoTracking()
                .Where(r => r.AddressVerifiedAtUtc != null && r.AddressVerifiedAtUtc >= cutoff && r.QueryLatitude != null && r.QueryLongitude != null)
                .OrderByDescending(r => r.AddressVerifiedAtUtc)
                .Take(MaxReuseCandidates)
                .Select(r => new { r.QueryLatitude, r.QueryLongitude, r.AddressCandidatesJson, r.AddressVerifyIntegrationLogId, r.AddressVerifiedAtUtc })
                .ToListAsync(cancellationToken);
            var hit = recent.FirstOrDefault(r => OpenserveQualificationEvidence.SameCoordinates(r.QueryLatitude, r.QueryLongitude, latitude, longitude));
            if (hit is not null)
            {
                var reused = OpenserveAddressCandidateMatcher.Read(hit.AddressCandidatesJson).Select(c => c.ToCandidate()).ToList();
                return new AddressVerification(reused, hit.AddressVerifyIntegrationLogId, hit.AddressVerifiedAtUtc!.Value, null);
            }
        }

        var result = await _client.QualifyAsync(new OpenserveQualificationQuery { Latitude = latitude, Longitude = longitude, BuildingInfo = true, ForceVerify = true }, cancellationToken);
        var now = DateTime.UtcNow;
        var verified = result.IsSuccess && result.Outcome?.Facts is { AddressVerifyReturned: true };
        var log = NewLog(result, now, verified, verified ? null
            : result.IsSuccess ? $"Address verification (FORCEVERIFY) returned no AddressVerify list ({context})." : $"{result.ErrorCode}: {result.ErrorMessage} (address verification, {context})");
        _dbContext.OpenserveIntegrationLogs.Add(log);

        if (!verified)
        {
            var failure = result.IsSuccess
                ? OpenserveApiCallResult<OpenserveQualificationOutcome>.Failure(result.MessageId, result.HttpMethod, result.Endpoint, result.HttpStatusCode, result.RequestBodyJson,
                    result.ResponseBodyJson, "NO_ADDRESS_VERIFY", "Openserve's address verification answer had no AddressVerify list.", result.RequestHeadersJson)
                : result;
            return new AddressVerification(Array.Empty<OpenserveAddressCandidate>(), log.Id, now, failure);
        }
        return new AddressVerification(result.Outcome!.Facts!.AddressCandidates ?? Array.Empty<OpenserveAddressCandidate>(), log.Id, now, null);
    }

    /// <summary>
    /// Qualifies one AMID (?AMID=…&amp;BuildingInfo=Y) — the answer that decides
    /// FTTH, products, speeds and building/unit for that premises. A very
    /// recent successful AMID qualification of the same AMID is reused when
    /// allowed. The returned row is not yet added to the context.
    /// </summary>
    private async Task<OpenserveQualificationResult> QualifyAmidAsync(string amid, OpenserveQualificationPurpose purpose, string context, bool allowReuse, CancellationToken cancellationToken)
    {
        var settings = _configProvider.Current;
        var now = DateTime.UtcNow;
        if (allowReuse && settings.Qualification.ReuseMinutes > 0)
        {
            var cutoff = now.AddMinutes(-settings.Qualification.ReuseMinutes);
            var sourceId = await _dbContext.OpenserveQualificationResults.AsNoTracking()
                .Where(r => r.QueryAmid == amid && r.Amid == amid && r.CallSucceeded && r.AddressIdentified && r.QualifiedAtUtc >= cutoff)
                .OrderByDescending(r => r.QualifiedAtUtc)
                .Select(r => (Guid?)r.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (sourceId is { } id)
            {
                var source = await _dbContext.OpenserveQualificationResults.AsNoTracking().Include(r => r.Products).FirstAsync(r => r.Id == id, cancellationToken);
                return OpenserveQualificationEvidence.CopyFacts(source, purpose, now);
            }
        }

        var result = await _client.QualifyAsync(new OpenserveQualificationQuery { Amid = amid, BuildingInfo = true }, cancellationToken);
        var log = NewLog(result, now, result.IsSuccess && !string.IsNullOrWhiteSpace(result.Outcome?.Amid),
            result.IsSuccess ? null : $"{result.ErrorCode}: {result.ErrorMessage} (AMID {amid}, {context})");
        _dbContext.OpenserveIntegrationLogs.Add(log);
        var evidence = OpenserveQualificationEvidence.Build(result, purpose, null, null, amid, log.Id, now);

        if (evidence.AddressIdentified && !string.Equals(evidence.Amid, amid, StringComparison.OrdinalIgnoreCase))
        {
            // An answer for another AMID doesn't describe the chosen premises.
            evidence.CallSucceeded = false;
            evidence.ErrorMessage = $"Openserve answered for AMID {evidence.Amid}, not the requested AMID {amid}.";
        }
        return evidence;
    }

    // ─── order runs ─────────────────────────────────────────────────

    private async Task RunForOrderSafeAsync(Order order, OpenserveQualificationPurpose purpose, bool allowReuse, CancellationToken cancellationToken)
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
            if (!AreUsable(latitude, longitude))
            {
                // Never call Openserve with missing or placeholder (0,0) coordinates.
                order.OpenserveQualifiedAtUtc = DateTime.UtcNow;
                if (string.IsNullOrWhiteSpace(order.OpenserveAmId)) order.OpenserveQualificationFailureReason = MissingCoordinatesReason;
                _logger.LogWarning("[Openserve][qualify] Order {OrderNumber} has no usable coordinates; qualification skipped.", order.OrderNumber);
                return;
            }

            var customer = OpenserveQualificationEvidence.CustomerAddressOf(order);
            var query = new OpenserveLocationQuery(latitude, longitude, customer.AddressLine1, customer.Suburb, customer.City, customer.Province, order.UnitNumber, order.BuildingComplexName);
            var evidence = await ResolvePremisesAsync(query, purpose, $"order {order.OrderNumber}", allowReuse, cancellationToken);

            // An earlier explicit choice (the customer's confirmed service location or an
            // Admin's) survives a re-run while Openserve still lists that premises near the customer.
            var previous = await PreviousEvidenceAsync(order.OpenserveQualificationResultId, cancellationToken);
            if (evidence.CallSucceeded && !evidence.AddressIdentified
                && previous is { AddressResolution: OpenserveAddressResolution.AdminSelected or OpenserveAddressResolution.CustomerSelected, Amid: { } chosenAmid }
                && OpenserveAddressCandidateMatcher.Read(evidence.AddressCandidatesJson).Any(c => string.Equals(c.Amid, chosenAmid, StringComparison.Ordinal)))
            {
                var kept = await QualifyAmidAsync(chosenAmid, purpose, $"order {order.OrderNumber}", allowReuse, cancellationToken);
                kept.QueryLatitude = evidence.QueryLatitude;
                kept.QueryLongitude = evidence.QueryLongitude;
                kept.AddressVerifiedAtUtc = evidence.AddressVerifiedAtUtc;
                kept.AddressVerifyIntegrationLogId = evidence.AddressVerifyIntegrationLogId;
                kept.AddressCandidateCount = evidence.AddressCandidateCount;
                kept.AddressCandidatesJson = evidence.AddressCandidatesJson;
                kept.AddressResolution = previous.AddressResolution;
                kept.AddressResolvedByUserId = previous.AddressResolvedByUserId;
                kept.AddressResolvedAtUtc = previous.AddressResolvedAtUtc;
                kept.AddressResolutionNote = previous.AddressResolutionNote;
                kept.AddressResolutionDetail = previous.AddressResolutionDetail;
                evidence = kept;
            }

            evidence.OrderId = order.Id;
            await EvaluateForOrderAsync(evidence, order, cancellationToken);
            await CarryOverAddressAcceptanceAsync(order.OpenserveQualificationResultId, evidence, cancellationToken);
            _dbContext.OpenserveQualificationResults.Add(evidence);
            await _dbContext.SaveChangesAsync(cancellationToken);

            ApplyToOrder(order, evidence, evidence.ErrorMessage);

            _logger.LogInformation("[Openserve][qualify] Order {OrderNumber}: premises {Resolution} AMID={Amid} fibre={Fibre} address={AddressMatch} product={Product} buildingNumId={BuildingNumId}",
                order.OrderNumber, evidence.AddressResolution, evidence.Amid, evidence.FibreAvailability, evidence.AddressMatch, evidence.ProductEligibility, order.OpenserveBuildingNumId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Openserve][qualify] Unexpected error qualifying order {OrderNumber}.", order.OrderNumber);
            order.OpenserveQualifiedAtUtc = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(order.OpenserveAmId)) order.OpenserveQualificationFailureReason = "An unexpected error occurred during Openserve qualification.";
        }
    }

    /// <summary>
    /// Copies a qualification's outcome onto the order. The AMID is stored only
    /// when the premises was established (matched or Admin-chosen); Fibre/
    /// product/address eligibility lives on the evidence the order points to.
    /// An unresolved verification clears an AMID the old nearest-address
    /// lookup had set — it was never established as the customer's. A failed
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
            ApplyServicePremises(order, evidence);

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
        ApplyServicePremises(order, null);
        order.OpenserveBuildingNumId = null;
        order.OpenserveBuildingName = null;
        order.OpenserveFloor = null;
        order.OpenserveUnit = null;
        order.OpenserveBuildingCandidateCount = null;
        order.OpenserveBuildingCandidatesJson = null;
        order.OpenserveQualificationFailureReason = !evidence.CallSucceeded
            ? Truncate(callError ?? evidence.ErrorMessage ?? "Qualification lookup failed.", 500)
            : evidence.AddressResolution is OpenserveAddressResolution.Unresolved or OpenserveAddressResolution.NoCandidates
                ? Truncate($"Openserve premises not established — {evidence.AddressResolutionDetail}", 500)
                : "Openserve returned no AMID for this address.";
    }

    /// <summary>
    /// The order's Openserve SERVICE PREMISES snapshot (never the installation address): Openserve's record text, how it
    /// was established (automatic match / customer's confirmed choice / Admin's choice / legacy), when, by whom, the
    /// distance AddressVerify reported and the customer's confirmation. Null evidence clears it.
    /// </summary>
    private static void ApplyServicePremises(Order order, OpenserveQualificationResult? evidence)
    {
        if (evidence is null)
        {
            order.OpenservePremisesAddress = null;
            order.OpenservePremisesSelection = OpenserveAddressResolution.NotEvaluated;
            order.OpenservePremisesSelectedAtUtc = null;
            order.OpenservePremisesSelectedByUserId = null;
            order.OpenservePremisesDistanceMeters = null;
            order.OpenservePremisesCustomerConfirmedAtUtc = null;
            return;
        }

        var chosenByPerson = evidence.AddressResolution is OpenserveAddressResolution.CustomerSelected or OpenserveAddressResolution.AdminSelected;
        var candidate = OpenserveAddressCandidateMatcher.Read(evidence.AddressCandidatesJson).FirstOrDefault(c => string.Equals(c.Amid, evidence.Amid, StringComparison.Ordinal));
        order.OpenservePremisesAddress = Truncate(evidence.CanonicalAddress ?? candidate?.Address, 300);
        order.OpenservePremisesSelection = evidence.AddressResolution;
        order.OpenservePremisesSelectedAtUtc = chosenByPerson ? evidence.AddressResolvedAtUtc : evidence.AddressVerifiedAtUtc ?? evidence.QualifiedAtUtc;
        order.OpenservePremisesSelectedByUserId = chosenByPerson ? evidence.AddressResolvedByUserId : null;
        order.OpenservePremisesDistanceMeters = candidate?.DistanceMeters ?? evidence.DistanceMeters;
        order.OpenservePremisesCustomerConfirmedAtUtc = evidence.AddressResolution == OpenserveAddressResolution.CustomerSelected ? evidence.AddressResolvedAtUtc : null;
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
        _logger.LogInformation("[Openserve][qualify] Order {OrderNumber} uses checkout qualification {EvidenceId}: premises {Resolution} AMID={Amid} fibre={Fibre} product={Product}",
            order.OrderNumber, evidence.Id, evidence.AddressResolution, evidence.Amid, evidence.FibreAvailability, evidence.ProductEligibility);
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

    private Task<OpenserveQualificationResult?> PreviousEvidenceAsync(Guid? evidenceId, CancellationToken cancellationToken) =>
        evidenceId is { } id
            ? _dbContext.OpenserveQualificationResults.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            : Task.FromResult<OpenserveQualificationResult?>(null);

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

        var evidence = order.OpenserveQualificationResultId is { } id
            ? await _dbContext.OpenserveQualificationResults.AsNoTracking().Include(r => r.Products).FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            : null;
        if (string.IsNullOrWhiteSpace(order.OpenserveAmId))
        {
            var unresolved = evidence is { CallSucceeded: true, AddressResolution: OpenserveAddressResolution.Unresolved or OpenserveAddressResolution.NoCandidates };
            return new OpenserveQualificationRunResult(unresolved ? OpenserveQualificationRunStatus.AddressUnresolved : OpenserveQualificationRunStatus.NoAmid,
                order.OpenserveQualificationFailureReason ?? "Openserve returned no AMID for this address.");
        }

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

    private async Task AuditAsync(Order order, OpenserveQualificationTrigger trigger, OpenserveQualificationRunResult result, string? previousAmid = null)
    {
        if (_auditService is null) return;
        try
        {
            var success = result.Status == OpenserveQualificationRunStatus.Qualified;
            var summary = success
                ? $"Openserve Product Qualification ({trigger}) for order {order.OrderNumber}: AMID {result.AmId} captured — {(result.FibreEligible == true ? "eligible" : "not eligible")}."
                : result.Status == OpenserveQualificationRunStatus.AddressUnresolved
                    ? $"Openserve address verification ({trigger}) for order {order.OrderNumber}: the customer's premises was not established — {result.Message}"
                    : $"Openserve Product Qualification ({trigger}) for order {order.OrderNumber} did not return an AMID: {result.Message}";
            var evidence = order.OpenserveQualificationResultId is { } id
                ? await _dbContext.OpenserveQualificationResults.AsNoTracking().Where(r => r.Id == id)
                    .Select(r => new { r.FibreAvailability, r.AddressMatch, r.ProductEligibility, r.CanonicalAddress, r.AddressResolution, r.AddressCandidateCount }).FirstOrDefaultAsync()
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
                    previousAmid,
                    buildingNumId = order.OpenserveBuildingNumId,
                    buildingCandidates = order.OpenserveBuildingCandidateCount,
                    reason = order.OpenserveQualificationFailureReason,
                    eligible = result.FibreEligible,
                    evidenceId = order.OpenserveQualificationResultId,
                    addressResolution = evidence?.AddressResolution.ToString(),
                    addressCandidates = evidence?.AddressCandidateCount,
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

    private async Task AuditCandidateSelectionAsync(Order order, StoredAddressCandidate chosen, OpenserveQualificationResult evidence, string? previousAmid, OpenserveQualificationRunResult result)
    {
        if (_auditService is null) return;
        try
        {
            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser?.UserId,
                ActorType = _currentUser?.UserId is not null ? AuditActorType.Admin : AuditActorType.System,
                ActionType = AuditActionType.OpenserveAddressCandidateSelected,
                EntityType = AuditEntityType.Order,
                EntityId = order.Id,
                EntityName = order.OrderNumber,
                Summary = Truncate($"Openserve premises for order {order.OrderNumber} set by Admin to {chosen.Address} (AMID {chosen.Amid}); customer address {evidence.CustomerAddress}.", 1000)!,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    customerAddress = evidence.CustomerAddress,
                    customerLatitude = evidence.QueryLatitude,
                    customerLongitude = evidence.QueryLongitude,
                    selectedAmid = chosen.Amid,
                    selectedAddress = chosen.Address,
                    distanceMeters = chosen.DistanceMeters,
                    automaticMatch = chosen.Match.ToString(),
                    matchDetail = chosen.MatchDetail,
                    reason = evidence.AddressResolutionNote,
                    previousAmid,
                    evidenceId = evidence.Id,
                    qualificationSucceeded = evidence.CallSucceeded,
                    fibre = evidence.FibreAvailability.ToString(),
                    productEligibility = evidence.ProductEligibility.ToString(),
                    eligible = result.FibreEligible
                }),
                IpAddress = _currentUser?.IpAddress,
                UserAgent = _currentUser?.UserAgent,
                IsSuccess = evidence.CallSucceeded
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Openserve][verify] Audit write failed for order {OrderNumber}.", order.OrderNumber);
        }
    }

    private async Task AuditServicePremisesSelectionAsync(OpenserveQualificationResult verification, StoredAddressCandidate chosen, OpenserveQualificationResult evidence,
        OpenserveLocationEligibility result)
    {
        if (_auditService is null) return;
        try
        {
            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser?.UserId,
                ActorType = AuditActorType.User,
                ActionType = AuditActionType.OpenserveServicePremisesSelected,
                EntityType = AuditEntityType.OpenserveQualification,
                EntityId = evidence.Id,
                EntityName = Truncate(chosen.Address, 200),
                Summary = Truncate($"Customer chose Openserve service location {chosen.Address} for installation address {evidence.CustomerAddress} (no automatic match) and confirmed it.", 1000)!,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    selectionSource = "Customer",
                    customerConfirmed = true,
                    anonymous = _currentUser?.UserId is null,
                    installationAddress = evidence.CustomerAddress,
                    customerLatitude = evidence.QueryLatitude,
                    customerLongitude = evidence.QueryLongitude,
                    selectedAmid = chosen.Amid,
                    selectedAddress = chosen.Address,
                    distanceMeters = chosen.DistanceMeters,
                    automaticMatch = chosen.Match.ToString(),
                    matchDetail = chosen.MatchDetail,
                    verificationEvidenceId = verification.Id,
                    candidateCount = verification.AddressCandidateCount,
                    evidenceId = evidence.Id,
                    qualificationSucceeded = evidence.CallSucceeded,
                    fibre = evidence.FibreAvailability.ToString(),
                    eligiblePackages = result.Packages.Count(p => p.Assessment.IsEligible)
                }),
                IpAddress = _currentUser?.IpAddress,
                UserAgent = _currentUser?.UserAgent,
                IsSuccess = evidence.CallSucceeded
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Openserve][verify] Audit write failed for a customer's service-location choice.");
        }
    }

    private static OpenserveQualificationRunResult Skipped(string message) => new(OpenserveQualificationRunStatus.Skipped, message);

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max];
    }
}

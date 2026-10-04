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

namespace SmartFuture.Application.Openserve;

public class OpenserveQualificationService : IOpenserveQualificationService
{
    public const string MissingCoordinatesReason = "Product Qualification unavailable — installation coordinates are missing.";

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

    public async Task QualifyOrderAsync(Order order, CancellationToken cancellationToken = default)
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
                order.OpenserveQualificationFailureReason = MissingCoordinatesReason;
                _logger.LogWarning("[Openserve][qualify] Order {OrderNumber} has no usable coordinates; qualification skipped.", order.OrderNumber);
                return;
            }

            var result = await _client.QualifyAsync(new OpenserveQualificationQuery
            {
                Latitude = latitude!.Value,
                Longitude = longitude!.Value,
                BuildingInfo = true
            }, cancellationToken);

            _dbContext.OpenserveIntegrationLogs.Add(new OpenserveIntegrationLog
            {
                Id = Guid.NewGuid(),
                OpenserveOrderId = null, // no OpenserveOrder exists yet at qualification time
                Direction = OpenserveIntegrationDirection.Outbound,
                OperationType = OpenserveOperationType.ProductQualification,
                MessageId = result.MessageId,
                HttpMethod = result.HttpMethod,
                Endpoint = result.Endpoint,
                RequestHeadersJson = result.RequestHeadersJson,
                ResponseStatusCode = result.HttpStatusCode,
                ResponseBodyJson = Truncate(result.ResponseBodyJson, 50_000),
                OccurredAtUtc = now,
                IsSuccess = result.IsSuccess && !string.IsNullOrWhiteSpace(result.Outcome?.Amid),
                ErrorSummary = result.IsSuccess
                    ? (string.IsNullOrWhiteSpace(result.Outcome?.Amid) ? $"Qualified but no AMID returned for order {order.OrderNumber}." : null)
                    : Truncate($"{result.ErrorCode}: {result.ErrorMessage} (order {order.OrderNumber})", 500)
            });
            await _dbContext.SaveChangesAsync(cancellationToken);

            order.OpenserveQualifiedAtUtc = now;

            if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.Outcome?.Amid))
            {
                var outcome = result.Outcome!;
                // The AMID is stored whatever happens with building/unit
                // matching — AMID and buildingNumId are separate concerns.
                order.OpenserveAmId = outcome.Amid;

                // MDU: store every buildingInfo row Openserve returned and
                // pick the customer's own row only when that's deterministic.
                // Several rows and no match → building/unit unresolved, and
                // submission is blocked until Admin chooses (never guessed).
                OpenserveBuildingCandidates.Apply(order, outcome.Buildings, outcome.BuildingMatchCount, outcome.BuildingNumId);

                _logger.LogInformation("[Openserve][qualify] Order {OrderNumber} qualified: AMID={Amid} buildingNumId={BuildingNumId} buildingMatches={BuildingMatchCount} ftthStatus={FtthStatus}",
                    order.OrderNumber, order.OpenserveAmId, order.OpenserveBuildingNumId, outcome.BuildingMatchCount, outcome.FtthStatus);
            }
            else
            {
                order.OpenserveQualificationFailureReason = result.IsSuccess
                    ? "Openserve returned no AMID for this address."
                    : Truncate($"{result.ErrorMessage ?? "Qualification lookup failed."}", 500);

                _logger.LogWarning("[Openserve][qualify] Order {OrderNumber} qualification did not produce an AMID: {Reason}", order.OrderNumber, order.OpenserveQualificationFailureReason);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Openserve][qualify] Unexpected error qualifying order {OrderNumber}.", order.OrderNumber);
            order.OpenserveQualifiedAtUtc = DateTime.UtcNow;
            order.OpenserveQualificationFailureReason = "An unexpected error occurred during Openserve qualification.";
        }
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
                .Select(o => new { o.PackageType, o.OpenserveAmId, o.OpenserveBuildingNumId, o.OpenserveQualifiedAtUtc, o.OpenserveQualificationFailureReason })
                .FirstOrDefaultAsync(cancellationToken);
            if (current is null) return new OpenserveQualificationRunResult(OpenserveQualificationRunStatus.NotFound, "Order not found.");

            if (current.PackageType != ServicePackageType.Fibre)
                return Skipped("Not a Fibre order — Product Qualification does not apply.");

            // Never re-qualify (or overwrite) an AMID the order already has.
            if (!string.IsNullOrWhiteSpace(current.OpenserveAmId))
                return new OpenserveQualificationRunResult(OpenserveQualificationRunStatus.Skipped, "AMID already captured.", current.OpenserveAmId, current.OpenserveBuildingNumId);

            var settings = _configProvider.Current;
            if (!settings.Enabled) return Skipped("Openserve integration is disabled.");

            if (!ignoreCooldown && current.OpenserveQualifiedAtUtc is { } lastRun)
            {
                var cooldown = TimeSpan.FromMinutes(Math.Max(0, settings.SubmissionRecovery.QualificationCooldownMinutes));
                if (lastRun > DateTime.UtcNow - cooldown)
                    return Skipped($"Product Qualification already ran at {lastRun:yyyy-MM-dd HH:mm} UTC: {current.OpenserveQualificationFailureReason ?? "no AMID returned"}");
            }

            var order = await _dbContext.Orders.FirstAsync(o => o.Id == orderId, cancellationToken);
            var hasCoordinates = await HasUsableCoordinatesAsync(order, cancellationToken);
            await QualifyOrderAsync(order, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var result = !hasCoordinates
                ? new OpenserveQualificationRunResult(OpenserveQualificationRunStatus.NoCoordinates, MissingCoordinatesReason)
                : !string.IsNullOrWhiteSpace(order.OpenserveAmId)
                    ? new OpenserveQualificationRunResult(OpenserveQualificationRunStatus.Qualified,
                        order.OpenserveQualificationFailureReason ?? $"AMID {order.OpenserveAmId} captured.", order.OpenserveAmId, order.OpenserveBuildingNumId)
                    : new OpenserveQualificationRunResult(OpenserveQualificationRunStatus.NoAmid, order.OpenserveQualificationFailureReason ?? "Openserve returned no AMID for this address.");

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
            _dbContext.OpenserveIntegrationLogs.Add(new OpenserveIntegrationLog
            {
                Id = Guid.NewGuid(),
                Direction = OpenserveIntegrationDirection.Outbound,
                OperationType = OpenserveOperationType.ProductQualification,
                MessageId = result.MessageId,
                HttpMethod = result.HttpMethod,
                Endpoint = result.Endpoint,
                RequestHeadersJson = result.RequestHeadersJson,
                ResponseStatusCode = result.HttpStatusCode,
                ResponseBodyJson = Truncate(result.ResponseBodyJson, 50_000),
                OccurredAtUtc = now,
                IsSuccess = result.IsSuccess,
                ErrorSummary = result.IsSuccess ? null : Truncate($"{result.ErrorCode}: {result.ErrorMessage} (building refresh, order {order.OrderNumber})", 500)
            });
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

    /// <summary>Real coordinates only: both present, in range, and not the (0,0) placeholder.</summary>
    public static bool AreUsable(decimal? latitude, decimal? longitude) =>
        latitude is { } lat && longitude is { } lon && lat is >= -90m and <= 90m && lon is >= -180m and <= 180m && !(lat == 0m && lon == 0m);

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
                ? $"Openserve Product Qualification ({trigger}) for order {order.OrderNumber}: AMID {result.AmId} captured."
                : $"Openserve Product Qualification ({trigger}) for order {order.OrderNumber} did not return an AMID: {result.Message}";
            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser?.UserId,
                ActorType = _currentUser?.UserId is not null ? AuditActorType.Admin : AuditActorType.System,
                ActionType = AuditActionType.OpenserveOrderQualificationRun,
                EntityType = AuditEntityType.Order,
                EntityId = order.Id,
                EntityName = order.OrderNumber,
                Summary = summary,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    trigger = trigger.ToString(),
                    status = result.Status.ToString(),
                    amid = order.OpenserveAmId,
                    buildingNumId = order.OpenserveBuildingNumId,
                    buildingCandidates = order.OpenserveBuildingCandidateCount,
                    reason = order.OpenserveQualificationFailureReason
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

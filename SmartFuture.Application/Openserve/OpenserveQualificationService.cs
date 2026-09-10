using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Application.Openserve;

public class OpenserveQualificationService : IOpenserveQualificationService
{
    private readonly IAppDbContext _dbContext;
    private readonly IOpenserveApiClient _client;
    private readonly IOpenserveRuntimeConfigProvider _configProvider;
    private readonly ILogger<OpenserveQualificationService> _logger;

    public OpenserveQualificationService(
        IAppDbContext dbContext, IOpenserveApiClient client,
        IOpenserveRuntimeConfigProvider configProvider, ILogger<OpenserveQualificationService> logger)
    {
        _dbContext = dbContext;
        _client = client;
        _configProvider = configProvider;
        _logger = logger;
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

            if (!latitude.HasValue || !longitude.HasValue)
            {
                order.OpenserveQualifiedAtUtc = now;
                order.OpenserveQualificationFailureReason = "No coordinates available for this order's address — qualification could not run.";
                _logger.LogWarning("[Openserve][qualify] Order {OrderNumber} has no coordinates; qualification skipped.", order.OrderNumber);
                return;
            }

            var result = await _client.QualifyAsync(new OpenserveQualificationQuery
            {
                Latitude = latitude.Value,
                Longitude = longitude.Value,
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
                ResponseStatusCode = result.HttpStatusCode,
                ResponseBodyJson = Truncate(result.ResponseBodyJson, 50_000),
                OccurredAtUtc = now,
                IsSuccess = result.IsSuccess && !string.IsNullOrWhiteSpace(result.Outcome?.Amid),
                ErrorSummary = result.IsSuccess
                    ? (string.IsNullOrWhiteSpace(result.Outcome?.Amid) ? $"Qualified but no AMID returned for order {order.OrderNumber}." : null)
                    : Truncate($"{result.ErrorCode}: {result.ErrorMessage}", 500)
            });
            await _dbContext.SaveChangesAsync(cancellationToken);

            order.OpenserveQualifiedAtUtc = now;

            if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.Outcome?.Amid))
            {
                order.OpenserveAmId = result.Outcome!.Amid;
                order.OpenserveBuildingNumId = result.Outcome.BuildingNumId;
                order.OpenserveQualificationFailureReason = result.Outcome.BuildingMatchCount > 1
                    ? $"AMID captured, but {result.Outcome.BuildingMatchCount} building/unit matches were returned — buildingNumId left blank pending unit confirmation."
                    : null;

                _logger.LogInformation(
                    "[Openserve][qualify] Order {OrderNumber} qualified: AMID={Amid} buildingNumId={BuildingNumId} ftthStatus={FtthStatus}",
                    order.OrderNumber, order.OpenserveAmId, order.OpenserveBuildingNumId, result.Outcome.FtthStatus);
            }
            else
            {
                order.OpenserveQualificationFailureReason = result.IsSuccess
                    ? "Openserve returned no AMID for this address."
                    : Truncate($"{result.ErrorMessage ?? "Qualification lookup failed."}", 500);

                _logger.LogWarning(
                    "[Openserve][qualify] Order {OrderNumber} qualification did not produce an AMID: {Reason}",
                    order.OrderNumber, order.OpenserveQualificationFailureReason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Openserve][qualify] Unexpected error qualifying order {OrderNumber}.", order.OrderNumber);
            order.OpenserveQualifiedAtUtc = DateTime.UtcNow;
            order.OpenserveQualificationFailureReason = "An unexpected error occurred during Openserve qualification.";
        }
    }

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

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max];
    }
}

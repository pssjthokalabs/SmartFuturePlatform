using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Openserve;
using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Application.Openserve;

public class OpenserveInboundProcessor : IOpenserveInboundProcessor
{
    private static readonly JsonSerializerOptions ParseOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IAppDbContext _dbContext;
    private readonly IOpenserveOrderUpdatePipeline _pipeline;
    private readonly ILogger<OpenserveInboundProcessor> _logger;

    public OpenserveInboundProcessor(IAppDbContext dbContext, IOpenserveOrderUpdatePipeline pipeline, ILogger<OpenserveInboundProcessor> logger)
    {
        _dbContext = dbContext;
        _pipeline = pipeline;
        _logger = logger;
    }

    public async Task<OpenserveInboundProcessResult> ProcessCallbackAsync(
        string rawPayload, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        OpenserveResultEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<OpenserveResultEnvelope>(rawPayload, ParseOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[Openserve][callback] malformed JSON payload.");
            await LogAsync(null, OpenserveOperationType.CallbackInbound, rawPayload, isSuccess: false, errorSummary: "Malformed JSON.", cancellationToken);
            return new OpenserveInboundProcessResult { Accepted = true, Message = "Malformed payload logged for review." };
        }

        var openserveOrderId = envelope?.Payload?.Order?.Id ?? envelope?.Payload?.Id;
        var state = envelope?.Payload?.State;

        var logId = await LogAsync(null, OpenserveOperationType.CallbackInbound, rawPayload, isSuccess: true, errorSummary: null, cancellationToken);

        if (string.IsNullOrWhiteSpace(openserveOrderId))
        {
            _logger.LogWarning("[Openserve][callback] payload carried no order id — cannot correlate. resultCode={ResultCode}", envelope?.Result?.ResultCode);
            await UpdateLogOutcomeAsync(logId, isSuccess: false, "Callback payload had no order.id.", null, cancellationToken);
            return new OpenserveInboundProcessResult { Accepted = true, Message = "No order id in payload — logged, not correlated." };
        }

        if (string.IsNullOrWhiteSpace(state))
        {
            // A callback with an order id but no state still confirms
            // the order id itself — worth recording as "Acknowledged"
            // (the documented default the sync ack already claims) so
            // GET/event correlation has something concrete to match on,
            // rather than silently dropping a payload that DID confirm
            // the order made it into Openserve's system.
            state = "Acknowledged";
        }

        var outcome = await _pipeline.ApplyUpdateAsync(new OpenserveUpdateInput
        {
            OpenserveOrderId = openserveOrderId,
            RawState = state,
            Description = envelope?.Result?.ResultMsg,
            EventType = OpenserveEventType.Unknown,
            IntegrationLogId = logId,
            IsReconciliation = false
        }, cancellationToken);

        await UpdateLogOutcomeAsync(
            logId,
            isSuccess: outcome.Kind is not (OpenserveUpdateResultKind.UnknownOrder or OpenserveUpdateResultKind.Error),
            outcome.Kind == OpenserveUpdateResultKind.UnknownOrder ? $"Unmatched order id {openserveOrderId}." : null,
            outcome.OpenserveOrderId,
            cancellationToken);

        return new OpenserveInboundProcessResult { Accepted = true, Message = outcome.Kind.ToString() };
    }

    public async Task<OpenserveInboundProcessResult> ProcessEventAsync(
        string rawPayload, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        OpenserveEventEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<OpenserveEventEnvelope>(rawPayload, ParseOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[Openserve][event] malformed JSON payload.");
            await LogAsync(null, OpenserveOperationType.EventNotificationInbound, rawPayload, isSuccess: false, errorSummary: "Malformed JSON.", cancellationToken);
            return new OpenserveInboundProcessResult { Accepted = true, Message = "Malformed payload logged for review." };
        }

        var eventType = ParseEventType(envelope?.EventType);
        var bodyElement = envelope?.Event?.ProductOrder ?? envelope?.Event?.Cancelproductorder;

        var logId = await LogAsync(null, OpenserveOperationType.EventNotificationInbound, rawPayload, isSuccess: true, errorSummary: null, cancellationToken);

        if (bodyElement is null || bodyElement.Value.ValueKind != JsonValueKind.Object)
        {
            _logger.LogWarning("[Openserve][event] payload carried no productOrder/cancelproductorder body. eventType={EventType} eventId={EventId}", envelope?.EventType, envelope?.EventId);
            await UpdateLogOutcomeAsync(logId, isSuccess: false, "Event payload had no productOrder/cancelproductorder body.", null, cancellationToken);
            return new OpenserveInboundProcessResult { Accepted = true, Message = "No order body in event — logged, not correlated." };
        }

        var facts = OpenserveOrderFactsExtractor.Extract(bodyElement.Value);
        if (string.IsNullOrWhiteSpace(facts.Id))
        {
            await UpdateLogOutcomeAsync(logId, isSuccess: false, "Event order body had no id.", null, cancellationToken);
            return new OpenserveInboundProcessResult { Accepted = true, Message = "No order id in event body — logged, not correlated." };
        }

        var eventOccurredAtUtc = TryParseUtc(envelope?.TimeOcurred) ?? TryParseUtc(envelope?.EventTime);

        var outcome = await _pipeline.ApplyUpdateAsync(new OpenserveUpdateInput
        {
            OpenserveOrderId = facts.Id,
            RawState = facts.State ?? "Unknown",
            Description = envelope?.Description,
            EventType = eventType,
            OpenserveEventId = envelope?.EventId?.ToString(),
            CorrelationId = envelope?.CorrelationId,
            OrderName = facts.OrderName,
            EventOccurredAtUtc = eventOccurredAtUtc,
            IntegrationLogId = logId,
            IsReconciliation = false
        }, cancellationToken);

        await UpdateLogOutcomeAsync(
            logId,
            isSuccess: outcome.Kind is not (OpenserveUpdateResultKind.UnknownOrder or OpenserveUpdateResultKind.Error),
            outcome.Kind == OpenserveUpdateResultKind.UnknownOrder ? $"Unmatched order id {facts.Id}." : null,
            outcome.OpenserveOrderId,
            cancellationToken);

        return new OpenserveInboundProcessResult { Accepted = true, Message = outcome.Kind.ToString() };
    }

    private static OpenserveEventType ParseEventType(string? raw) => raw?.Trim() switch
    {
        "ProductOrderCreateEvent" => OpenserveEventType.ProductOrderCreateEvent,
        "ProductOrderStateChangeEvent" => OpenserveEventType.ProductOrderStateChangeEvent,
        "CancelProductOrderCreateEvent" => OpenserveEventType.CancelProductOrderCreateEvent,
        "CancelProductOrderStateChangeEvent" => OpenserveEventType.CancelProductOrderStateChangeEvent,
        _ => OpenserveEventType.Unknown
    };

    private static DateTime? TryParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.UtcDateTime
            : null;
    }

    private async Task<Guid> LogAsync(
        Guid? openserveOrderId, OpenserveOperationType operationType, string rawPayload,
        bool isSuccess, string? errorSummary, CancellationToken cancellationToken)
    {
        var log = new OpenserveIntegrationLog
        {
            Id = Guid.NewGuid(),
            OpenserveOrderId = openserveOrderId,
            Direction = OpenserveIntegrationDirection.Inbound,
            OperationType = operationType,
            HttpMethod = "POST",
            Endpoint = operationType == OpenserveOperationType.CallbackInbound ? "api/openserve/callback" : "api/openserve/events",
            RequestBodyJson = Truncate(rawPayload, 200_000),
            OccurredAtUtc = DateTime.UtcNow,
            IsSuccess = isSuccess,
            ErrorSummary = errorSummary
        };
        _dbContext.OpenserveIntegrationLogs.Add(log);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return log.Id;
    }

    private async Task UpdateLogOutcomeAsync(Guid logId, bool isSuccess, string? errorSummary, Guid? openserveOrderId, CancellationToken cancellationToken)
    {
        var log = await _dbContext.OpenserveIntegrationLogs.FindAsync(new object[] { logId }, cancellationToken);
        if (log is null) return;
        log.IsSuccess = isSuccess;
        log.ErrorSummary = errorSummary;
        if (openserveOrderId.HasValue) log.OpenserveOrderId = openserveOrderId;
        log.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

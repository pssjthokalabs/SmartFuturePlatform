using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Openserve;

/// <summary>
/// Backs the Admin → Integrations → Openserve console (all admin-only,
/// authorization enforced at the controller). Every diagnostic action
/// here is read-only against Smart Future's own order/network state —
/// only Synchronize (already on IOpenserveReconciliationService) can
/// mutate an existing OpenserveOrder, and only when the admin explicitly
/// triggers it on a KNOWN order.
/// </summary>
public interface IOpenserveIntegrationAdminService
{
    Task<Result<OpenserveIntegrationOverviewDto>> GetOverviewAsync(CancellationToken cancellationToken = default);

    Task<Result<OpenserveConfigurationDto>> GetConfigurationAsync(CancellationToken cancellationToken = default);

    Task<Result<OpenserveConfigurationDto>> UpdateConfigurationAsync(
        UpdateOpenserveConfigurationRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<OpenserveReadinessCheckDto>> RunReadinessCheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Local-only validation — no Openserve call. See brief §6: the "Configuration Check" half of Test Connection.</summary>
    Task<Result<OpenserveConfigurationCheckResultDto>> RunConfigurationCheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Real network call via the documented GET ACTIONS endpoint (spec §4.8.1) — the "Test Connection" half. Never creates/updates/cancels anything.</summary>
    Task<Result<OpenserveTestConnectionResultDto>> TestConnectionAsync(CancellationToken cancellationToken = default);

    Task<Result<OpenserveQualificationTestResultDto>> RunQualificationTestAsync(
        RunOpenserveQualificationTestRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<OpenserveOrderLookupTestResultDto>> RunOrderLookupTestAsync(
        string openserveOrderId, CancellationToken cancellationToken = default);

    Task<Result<OpenserveCallbackHealthDto>> GetCallbackHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>Global, filterable view of every OpenserveIntegrationLog row (outbound calls we made + inbound callbacks/events) — backs the Admin console "Logs" tab (brief §11) so support never needs Swagger/DB access to inspect a raw Openserve exchange.</summary>
    Task<Result<Common.Paging.PagedResult<OpenserveIntegrationLogDto>>> SearchIntegrationLogsAsync(
        OpenserveIntegrationLogFilterRequestDto filter, CancellationToken cancellationToken = default);
}

using SmartFuture.Shared.Results;

namespace SmartFuture.Application.NetworkAccounts;

/// <summary>
/// High-level orchestrator for network service provisioning lifecycle.
/// Wraps the provider-port <see cref="INetworkProvisioner"/> with audit,
/// idempotency, and persistence concerns. Implementations MUST:
///   - update NetworkAccount status + ProvisioningStatus atomically with the provider call
///   - write a ProvisioningEvent for every attempt (success or failure)
///   - be idempotent (re-invocation with the same target state is a no-op)
///   - support future async queue workers (commands carry CorrelationId)
/// The current NoOp implementation never touches a real router/RADIUS;
/// it only logs + advances DB state so the rest of the system can be
/// exercised end-to-end before MikroTik / FreeRADIUS integrations land.
/// </summary>
public interface INetworkProvisioningService
{
    Task<Result<ProvisioningOutcomeDto>> ActivateServiceAsync(
        ActivateServiceRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<ProvisioningOutcomeDto>> SuspendServiceAsync(
        SuspendServiceRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<ProvisioningOutcomeDto>> ResumeServiceAsync(
        ResumeServiceRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<ProvisioningOutcomeDto>> ChangePackageAsync(
        ChangePackageRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<ProvisioningOutcomeDto>> TerminateServiceAsync(
        TerminateServiceRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<ProvisioningOutcomeDto>> DisconnectSessionAsync(
        DisconnectSessionRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<SessionStatusDto>> GetSessionStatusAsync(
        Guid networkAccountId, CancellationToken cancellationToken = default);
}

public sealed record ActivateServiceRequestDto(
    Guid NetworkAccountId, Guid? TriggeredByUserId = null, string? CorrelationId = null);

public sealed record SuspendServiceRequestDto(
    Guid NetworkAccountId, string? Reason = null, Guid? TriggeredByUserId = null, string? CorrelationId = null);

public sealed record ResumeServiceRequestDto(
    Guid NetworkAccountId, Guid? TriggeredByUserId = null, string? CorrelationId = null);

public sealed record ChangePackageRequestDto(
    Guid NetworkAccountId, Guid NewPackageId, Guid? TriggeredByUserId = null, string? CorrelationId = null);

public sealed record TerminateServiceRequestDto(
    Guid NetworkAccountId, string? Reason = null, Guid? TriggeredByUserId = null, string? CorrelationId = null);

public sealed record DisconnectSessionRequestDto(
    Guid NetworkAccountId, Guid? TriggeredByUserId = null, string? CorrelationId = null);

public sealed record ProvisioningOutcomeDto(
    Guid NetworkAccountId, Guid? ProvisioningEventId, bool ProviderTouched, string Summary);

public sealed record SessionStatusDto(
    Guid NetworkAccountId, bool IsOnline, string? CurrentIpAddress, string? NasIdentifier,
    DateTime? LastSeenUtc, string ProviderName);

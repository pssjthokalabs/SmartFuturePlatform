using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.NetworkAccounts;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Infrastructure.Configuration;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Infrastructure.NetworkAccounts;

/// <summary>
/// Stub <see cref="INetworkProvisioningService"/> for the pre-RADIUS phase.
/// Updates NetworkAccount status + writes ProvisioningEvent rows so the rest of
/// the platform (orders, payments, admin) can be exercised end-to-end, but
/// never touches a real router or RADIUS server. Replace in DI with a real
/// implementation that orchestrates <see cref="INetworkProvisioner"/> when
/// FreeRADIUS / MikroTik integration lands.
/// </summary>
public class NoOpNetworkProvisioningService : INetworkProvisioningService
{
    private const string ProviderName = "NoOp";

    private readonly IAppDbContext _dbContext;
    private readonly ILogger<NoOpNetworkProvisioningService> _logger;
    private readonly IOptions<ProvisioningSettings> _options;

    public NoOpNetworkProvisioningService(
        IAppDbContext dbContext,
        ILogger<NoOpNetworkProvisioningService> logger,
        IOptions<ProvisioningSettings> options)
    {
        _dbContext = dbContext;
        _logger = logger;
        _options = options;
    }

    // Phase 3.6 — single guard helper. Every state-mutating action
    // checks here first. Disabled returns a uniform, user-presentable
    // error so the portal can surface it without inspecting codes.
    // Reads through IOptions each call so a config reload takes
    // effect without a restart.
    private Result<ProvisioningOutcomeDto>? GuardActionable()
    {
        var settings = _options.Value;
        if (!settings.Enabled)
        {
            _logger.LogInformation("[Provisioning:NoOp] Action blocked — Provisioning.Enabled=false");
            return Result<ProvisioningOutcomeDto>.Failure(
                ErrorCodes.SERVICE_UNAVAILABLE, "Provisioning actions are currently disabled.");
        }
        if (settings.Mode != ProvisioningMode.NoOp)
        {
            _logger.LogInformation(
                "[Provisioning:NoOp] Action blocked — mode={Mode} (only NoOp is honoured)", settings.Mode);
            return Result<ProvisioningOutcomeDto>.Failure(
                ErrorCodes.SERVICE_UNAVAILABLE,
                $"Provisioning mode is '{settings.Mode}'; only the NoOp simulation is currently honoured.");
        }
        return null;
    }

    public async Task<Result<ProvisioningOutcomeDto>> ActivateServiceAsync(
        ActivateServiceRequestDto request, CancellationToken cancellationToken = default)
    {
        var blocked = GuardActionable();
        if (blocked is not null) return blocked;
        var account = await _dbContext.NetworkAccounts.FirstOrDefaultAsync(a => a.Id == request.NetworkAccountId, cancellationToken);
        if (account is null) return NotFound(request.NetworkAccountId);

        if (account.Status == NetworkAccountStatus.Active && account.ProvisioningStatus == ProvisioningStatus.Provisioned)
        {
            _logger.LogInformation("[Provisioning:NoOp] Activate idempotent skip account={Id}", account.Id);
            return Ok(account.Id, null, providerTouched: false, "Account already active.");
        }

        var now = DateTime.UtcNow;
        account.Status = NetworkAccountStatus.Active;
        account.ProvisioningStatus = ProvisioningStatus.Provisioned;
        account.LastProvisioningAttemptUtc = now;
        account.ProvisioningAttemptCount++;
        account.ProvisionedAtUtc ??= now;
        if (string.IsNullOrWhiteSpace(account.ProviderName)) account.ProviderName = ProviderName;

        var ev = AddEvent(account, ProvisioningEventType.Activated, true, "NoOp activate — DB-only.", request.TriggeredByUserId);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[Provisioning:NoOp] Activated account={Id} eventId={EventId} corr={Corr}", account.Id, ev.Id, request.CorrelationId);
        return Ok(account.Id, ev.Id, providerTouched: false, "Activated (NoOp).");
    }

    public async Task<Result<ProvisioningOutcomeDto>> SuspendServiceAsync(
        SuspendServiceRequestDto request, CancellationToken cancellationToken = default)
    {
        var blocked = GuardActionable();
        if (blocked is not null) return blocked;
        var account = await _dbContext.NetworkAccounts.FirstOrDefaultAsync(a => a.Id == request.NetworkAccountId, cancellationToken);
        if (account is null) return NotFound(request.NetworkAccountId);

        if (account.Status == NetworkAccountStatus.Suspended)
        {
            return Ok(account.Id, null, providerTouched: false, "Account already suspended.");
        }

        var now = DateTime.UtcNow;
        account.Status = NetworkAccountStatus.Suspended;
        account.SuspendedAtUtc = now;
        account.SuspensionReason = request.Reason;
        account.LastProvisioningAttemptUtc = now;

        var ev = AddEvent(account, ProvisioningEventType.Suspended, true, request.Reason ?? "Suspended (NoOp).", request.TriggeredByUserId);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[Provisioning:NoOp] Suspended account={Id} reason={Reason} eventId={EventId}", account.Id, request.Reason, ev.Id);
        return Ok(account.Id, ev.Id, providerTouched: false, "Suspended (NoOp).");
    }

    public async Task<Result<ProvisioningOutcomeDto>> ResumeServiceAsync(
        ResumeServiceRequestDto request, CancellationToken cancellationToken = default)
    {
        var blocked = GuardActionable();
        if (blocked is not null) return blocked;
        var account = await _dbContext.NetworkAccounts.FirstOrDefaultAsync(a => a.Id == request.NetworkAccountId, cancellationToken);
        if (account is null) return NotFound(request.NetworkAccountId);

        if (account.Status == NetworkAccountStatus.Active)
        {
            return Ok(account.Id, null, providerTouched: false, "Account already active.");
        }

        var now = DateTime.UtcNow;
        account.Status = NetworkAccountStatus.Active;
        account.ResumedAtUtc = now;
        account.SuspensionReason = null;
        account.LastProvisioningAttemptUtc = now;

        var ev = AddEvent(account, ProvisioningEventType.Resumed, true, "Resumed (NoOp).", request.TriggeredByUserId);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[Provisioning:NoOp] Resumed account={Id} eventId={EventId}", account.Id, ev.Id);
        return Ok(account.Id, ev.Id, providerTouched: false, "Resumed (NoOp).");
    }

    public async Task<Result<ProvisioningOutcomeDto>> ChangePackageAsync(
        ChangePackageRequestDto request, CancellationToken cancellationToken = default)
    {
        var blocked = GuardActionable();
        if (blocked is not null) return blocked;
        var account = await _dbContext.NetworkAccounts.FirstOrDefaultAsync(a => a.Id == request.NetworkAccountId, cancellationToken);
        if (account is null) return NotFound(request.NetworkAccountId);

        var pkg = await _dbContext.ServicePackages.FirstOrDefaultAsync(p => p.Id == request.NewPackageId, cancellationToken);
        if (pkg is null)
            return Result<ProvisioningOutcomeDto>.Failure(ErrorCodes.NOT_FOUND, $"ServicePackage {request.NewPackageId} not found.");

        var now = DateTime.UtcNow;
        account.PackageName = pkg.Name;
        account.PackageType = pkg.Type;
        account.PackageSpeedLabel = pkg.SpeedLabel;
        account.PackagePrice = pkg.Price;
        account.RadiusProfileId = pkg.RadiusProfileId;
        account.LastPackageChangeAtUtc = now;
        account.LastProvisioningAttemptUtc = now;

        var ev = AddEvent(account, ProvisioningEventType.PackageChanged, true, $"Package changed to {pkg.Name} (NoOp).", request.TriggeredByUserId);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[Provisioning:NoOp] PackageChanged account={Id} package={Pkg} eventId={EventId}", account.Id, pkg.Name, ev.Id);
        return Ok(account.Id, ev.Id, providerTouched: false, $"Package changed to {pkg.Name} (NoOp).");
    }

    public async Task<Result<ProvisioningOutcomeDto>> TerminateServiceAsync(
        TerminateServiceRequestDto request, CancellationToken cancellationToken = default)
    {
        var blocked = GuardActionable();
        if (blocked is not null) return blocked;
        var account = await _dbContext.NetworkAccounts.FirstOrDefaultAsync(a => a.Id == request.NetworkAccountId, cancellationToken);
        if (account is null) return NotFound(request.NetworkAccountId);

        if (account.Status == NetworkAccountStatus.Terminated)
        {
            return Ok(account.Id, null, providerTouched: false, "Account already terminated.");
        }

        var now = DateTime.UtcNow;
        account.Status = NetworkAccountStatus.Terminated;
        account.TerminatedAtUtc = now;
        account.TerminationReason = request.Reason;
        account.LastProvisioningAttemptUtc = now;

        var ev = AddEvent(account, ProvisioningEventType.Terminated, true, request.Reason ?? "Terminated (NoOp).", request.TriggeredByUserId);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[Provisioning:NoOp] Terminated account={Id} reason={Reason} eventId={EventId}", account.Id, request.Reason, ev.Id);
        return Ok(account.Id, ev.Id, providerTouched: false, "Terminated (NoOp).");
    }

    public Task<Result<ProvisioningOutcomeDto>> DisconnectSessionAsync(
        DisconnectSessionRequestDto request, CancellationToken cancellationToken = default)
    {
        var blocked = GuardActionable();
        if (blocked is not null) return Task.FromResult(blocked);
        _logger.LogInformation("[Provisioning:NoOp] DisconnectSession requested account={Id} (no real session to drop)", request.NetworkAccountId);
        return Task.FromResult(Result<ProvisioningOutcomeDto>.Success(
            new ProvisioningOutcomeDto(request.NetworkAccountId, null, false, "No live session in NoOp mode.")));
    }

    public async Task<Result<SessionStatusDto>> GetSessionStatusAsync(
        Guid networkAccountId, CancellationToken cancellationToken = default)
    {
        var account = await _dbContext.NetworkAccounts.FirstOrDefaultAsync(a => a.Id == networkAccountId, cancellationToken);
        if (account is null)
            return Result<SessionStatusDto>.Failure(ErrorCodes.NOT_FOUND, $"NetworkAccount {networkAccountId} not found.");

        return Result<SessionStatusDto>.Success(new SessionStatusDto(
            networkAccountId, IsOnline: false, account.CurrentIpAddress, account.NasIdentifier,
            LastSeenUtc: null, ProviderName));
    }

    private ProvisioningEvent AddEvent(NetworkAccount account, ProvisioningEventType type, bool success, string? summary, Guid? userId)
    {
        var ev = new ProvisioningEvent
        {
            Id = Guid.NewGuid(),
            NetworkAccountId = account.Id,
            EventType = type,
            ProviderName = ProviderName,
            IsSuccess = success,
            Summary = summary,
            TriggeredByUserId = userId,
            CreatedAtUtc = DateTime.UtcNow
        };
        _dbContext.ProvisioningEvents.Add(ev);
        return ev;
    }

    private static Result<ProvisioningOutcomeDto> NotFound(Guid id) =>
        Result<ProvisioningOutcomeDto>.Failure(ErrorCodes.NOT_FOUND, $"NetworkAccount {id} not found.");

    private static Result<ProvisioningOutcomeDto> Ok(Guid accountId, Guid? eventId, bool providerTouched, string summary) =>
        Result<ProvisioningOutcomeDto>.Success(new ProvisioningOutcomeDto(accountId, eventId, providerTouched, summary));
}

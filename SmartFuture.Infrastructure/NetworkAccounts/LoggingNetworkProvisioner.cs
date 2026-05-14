using Microsoft.Extensions.Logging;
using SmartFuture.Application.NetworkAccounts;

namespace SmartFuture.Infrastructure.NetworkAccounts;

/// <summary>
/// Placeholder INetworkProvisioner that logs operations and returns synthetic provider
/// references. Mirrors the LoggingNotificationSender pattern. MUST NOT be used in
/// production — real provisioning requires a FreeRADIUS/MikroTik/vendor implementation
/// to be wired up in Phase 12-Live.
/// </summary>
public class LoggingNetworkProvisioner : INetworkProvisioner
{
    private const string ProviderRefPrefix = "LOGGING-";

    private readonly ILogger<LoggingNetworkProvisioner> _logger;

    public LoggingNetworkProvisioner(ILogger<LoggingNetworkProvisioner> logger)
    {
        _logger = logger;
    }

    public string ProviderName => "Logging";

    public Task<NetworkProvisioningResult> ProvisionAsync(
        NetworkProvisioningContext context, CancellationToken cancellationToken = default)
    {
        var providerReference = $"{ProviderRefPrefix}{Guid.NewGuid():N}";
        _logger.LogInformation(
            "[NetworkProvisioner:Logging] Provision account={AccountNumber} username={Username} order={OrderNumber} package={PackageName} providerRef={ProviderRef}",
            context.AccountNumber, context.Username, context.OrderNumber, context.PackageName, providerReference);
        return Task.FromResult(NetworkProvisioningResult.Success(providerReference));
    }

    public Task<NetworkProvisioningResult> SuspendAsync(
        NetworkProvisioningContext context, string? reason, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[NetworkProvisioner:Logging] Suspend account={AccountNumber} username={Username} reason={Reason}",
            context.AccountNumber, context.Username, reason);
        return Task.FromResult(NetworkProvisioningResult.Success());
    }

    public Task<NetworkProvisioningResult> ResumeAsync(
        NetworkProvisioningContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[NetworkProvisioner:Logging] Resume account={AccountNumber} username={Username}",
            context.AccountNumber, context.Username);
        return Task.FromResult(NetworkProvisioningResult.Success());
    }

    public Task<NetworkProvisioningResult> TerminateAsync(
        NetworkProvisioningContext context, string? reason, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[NetworkProvisioner:Logging] Terminate account={AccountNumber} username={Username} reason={Reason}",
            context.AccountNumber, context.Username, reason);
        return Task.FromResult(NetworkProvisioningResult.Success());
    }

    public Task<NetworkProvisioningResult> ChangePackageAsync(
        NetworkProvisioningContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[NetworkProvisioner:Logging] ChangePackage account={AccountNumber} newPackage={PackageName} type={PackageType}",
            context.AccountNumber, context.PackageName, context.PackageType);
        return Task.FromResult(NetworkProvisioningResult.Success());
    }
}

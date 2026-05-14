namespace SmartFuture.Application.NetworkAccounts;

/// <summary>
/// Provider port for network access provisioning. Implementations integrate with a specific
/// network platform (e.g., FreeRADIUS, MikroTik RouterOS, vendor APIs).
/// Implementations MUST NOT store plaintext customer credentials in their return values.
/// Only opaque provider references should round-trip through this contract.
/// </summary>
public interface INetworkProvisioner
{
    string ProviderName { get; }

    Task<NetworkProvisioningResult> ProvisionAsync(
        NetworkProvisioningContext context, CancellationToken cancellationToken = default);

    Task<NetworkProvisioningResult> SuspendAsync(
        NetworkProvisioningContext context, string? reason, CancellationToken cancellationToken = default);

    Task<NetworkProvisioningResult> ResumeAsync(
        NetworkProvisioningContext context, CancellationToken cancellationToken = default);

    Task<NetworkProvisioningResult> TerminateAsync(
        NetworkProvisioningContext context, string? reason, CancellationToken cancellationToken = default);

    Task<NetworkProvisioningResult> ChangePackageAsync(
        NetworkProvisioningContext context, CancellationToken cancellationToken = default);
}

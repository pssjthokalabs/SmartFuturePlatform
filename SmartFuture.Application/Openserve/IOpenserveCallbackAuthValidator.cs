using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Application.Openserve;

/// <summary>
/// Validates an inbound Openserve callback/event request against
/// whatever auth mechanism is configured. The Fulfilment API Spec does
/// NOT document any callback/event authentication scheme (no HMAC, no
/// shared secret, no IP allowlist) — this is a real gap on Openserve's
/// side, not something safe to guess at. Mode defaults to None, which
/// means "accept the request" (there is nothing else we can validate
/// against without inventing a contract Openserve never specified);
/// safety instead comes from OpenserveFulfilment:Enabled staying false
/// until Openserve confirms a real mechanism, and from the update
/// pipeline's own correlation requirement (an attacker sending us
/// garbage cannot affect any order they don't already know the
/// Openserve order id for, and even then can only feed status noise
/// into history, never trigger billing without also matching AMID/
/// mapping/eligibility rules elsewhere).
/// SharedSecretHeader/IpAllowlist are implemented so flipping the
/// config is all that's needed the moment Openserve confirms a scheme.
/// </summary>
public interface IOpenserveCallbackAuthValidator
{
    bool IsValid(IReadOnlyDictionary<string, string> headers, string? remoteIp);
}

public class OpenserveCallbackAuthValidator : IOpenserveCallbackAuthValidator
{
    public const string SharedSecretHeaderName = "x-openserve-shared-secret";

    private readonly IOpenserveRuntimeConfigProvider _configProvider;

    public OpenserveCallbackAuthValidator(IOpenserveRuntimeConfigProvider configProvider)
    {
        _configProvider = configProvider;
    }

    public bool IsValid(IReadOnlyDictionary<string, string> headers, string? remoteIp)
    {
        var auth = _configProvider.Current.CallbackAuth;

        return auth.Mode switch
        {
            OpenserveCallbackAuthMode.None => true,

            OpenserveCallbackAuthMode.SharedSecretHeader =>
                !string.IsNullOrWhiteSpace(auth.SharedSecret)
                && headers.TryGetValue(SharedSecretHeaderName, out var provided)
                && string.Equals(provided, auth.SharedSecret, StringComparison.Ordinal),

            // Literal-IP match only (no CIDR parsing) — deliberately
            // simple until Openserve confirms whether IP allowlisting
            // is even the mechanism they intend to use.
            OpenserveCallbackAuthMode.IpAllowlist =>
                !string.IsNullOrWhiteSpace(remoteIp)
                && auth.AllowedIpRanges.Any(ip => string.Equals(ip.Trim(), remoteIp, StringComparison.OrdinalIgnoreCase)),

            _ => false
        };
    }
}

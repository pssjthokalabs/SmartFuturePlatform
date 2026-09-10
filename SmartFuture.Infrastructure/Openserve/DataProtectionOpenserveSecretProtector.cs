using Microsoft.AspNetCore.DataProtection;
using SmartFuture.Application.Openserve;

namespace SmartFuture.Infrastructure.Openserve;

/// <summary>
/// ASP.NET Core DataProtection-backed implementation of
/// <see cref="IOpenserveSecretProtector"/>. Reuses the SAME key ring
/// already registered for the whole app (see
/// <c>services.AddDataProtection().SetApplicationName("SmartFuture.API")</c>
/// in ServiceExtensions, the identical mechanism
/// <c>DataProtectionMandateProtector</c> already uses for Paystack
/// mandate authorization codes) — no new key-storage setup needed.
///
/// Same known limitation as that existing usage: the default OS-managed
/// key store is fine for single-instance hosting; a multi-instance
/// deploy needs a shared persisted key store (file share / Azure Key
/// Vault / Blob) or each instance decrypts with a different key and
/// stored secrets become unreadable after a restart on a different
/// instance. Not introduced by this change — pre-existing or the whole
/// app's DataProtection usage.
/// </summary>
public class DataProtectionOpenserveSecretProtector : IOpenserveSecretProtector
{
    private const string ApiKeyPurpose = "Openserve.ApiKey.v1";
    private const string SharedSecretPurpose = "Openserve.SharedSecret.v1";

    private readonly IDataProtector _apiKeyProtector;
    private readonly IDataProtector _sharedSecretProtector;

    public DataProtectionOpenserveSecretProtector(IDataProtectionProvider provider)
    {
        _apiKeyProtector = provider.CreateProtector(ApiKeyPurpose);
        _sharedSecretProtector = provider.CreateProtector(SharedSecretPurpose);
    }

    public string ProtectApiKey(string plaintext) => _apiKeyProtector.Protect(plaintext);
    public string UnprotectApiKey(string protectedBlob) => _apiKeyProtector.Unprotect(protectedBlob);

    public string ProtectSharedSecret(string plaintext) => _sharedSecretProtector.Protect(plaintext);
    public string UnprotectSharedSecret(string protectedBlob) => _sharedSecretProtector.Unprotect(protectedBlob);
}

namespace SmartFuture.Application.Openserve;

/// <summary>
/// Thin protector abstraction over ASP.NET Core DataProtection so
/// Application code stays free of a Microsoft.AspNetCore.* dependency —
/// same shape as <c>IMandateProtector</c> (which already protects
/// <c>CustomerPaymentMandate.AuthorizationCode</c> using the same,
/// already-registered <c>IDataProtectionProvider</c>). Two separate
/// purposes (one per secret) so an ApiKey ciphertext can never be fed
/// into the SharedSecret unprotect path or vice versa.
///
/// Protected blobs are opaque — never log them, never return them to
/// any frontend. Only <c>OpenserveRuntimeConfigProvider</c> unprotects,
/// and only in memory for the lifetime of the resolved settings.
/// </summary>
public interface IOpenserveSecretProtector
{
    string ProtectApiKey(string plaintext);
    string UnprotectApiKey(string protectedBlob);

    string ProtectSharedSecret(string plaintext);
    string UnprotectSharedSecret(string protectedBlob);
}

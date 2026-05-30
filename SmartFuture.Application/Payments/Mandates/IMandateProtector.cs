namespace SmartFuture.Application.Payments.Mandates;

/// <summary>
/// Thin protector abstraction over ASP.NET Core DataProtection so the
/// mandate service stays in Application (no Microsoft.AspNetCore.*
/// dependency). The Infrastructure implementation wires this to
/// <c>IDataProtectionProvider.CreateProtector("CustomerPaymentMandate.AuthorizationCode")</c>.
///
/// The protected blob is opaque to callers — never log it, never
/// surface it to clients. Only <c>PaystackChargeAuthorizationService</c>
/// unprotects when issuing a charge.
/// </summary>
public interface IMandateProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedBlob);
}

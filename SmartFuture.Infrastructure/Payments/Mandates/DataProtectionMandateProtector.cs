using Microsoft.AspNetCore.DataProtection;
using SmartFuture.Application.Payments.Mandates;

namespace SmartFuture.Infrastructure.Payments.Mandates;

/// <summary>
/// ASP.NET Core DataProtection-backed implementation of
/// <see cref="IMandateProtector"/>. Purpose string is pinned so a
/// future unrelated protector cannot accidentally unprotect mandate
/// blobs. Keys persist to the OS-managed key ring by default (see
/// AddDataProtection() in ServiceExtensions).
/// </summary>
public class DataProtectionMandateProtector : IMandateProtector
{
    private const string Purpose = "CustomerPaymentMandate.AuthorizationCode.v1";

    private readonly IDataProtector _protector;

    public DataProtectionMandateProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);
    public string Unprotect(string protectedBlob) => _protector.Unprotect(protectedBlob);
}

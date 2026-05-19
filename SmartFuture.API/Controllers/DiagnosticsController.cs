using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Billing;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

/// <summary>
/// Admin-only runtime diagnostics. Currently exposes one endpoint:
/// the resolved <c>PaymentSettings</c> + environment name so operators
/// can confirm a UAT app pool actually loaded the
/// <c>PaymentSettings__MockCheckoutEnabled=true</c> env var.
///
/// Never returns secrets — no connection strings, no JWT keys, no SMTP
/// credentials. Only boolean flags and environment metadata.
/// </summary>
[Route("api/diagnostics")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class DiagnosticsController : BaseController
{
    private readonly IOptions<PaymentSettings> _paymentSettings;
    private readonly IHostEnvironment _hostEnvironment;

    public DiagnosticsController(IOptions<PaymentSettings> paymentSettings, IHostEnvironment hostEnvironment)
    {
        _paymentSettings = paymentSettings;
        _hostEnvironment = hostEnvironment;
    }

    /// <summary>
    /// GET /api/diagnostics/config/payment-settings — returns only the
    /// boolean gate + environment metadata. Admin role required.
    /// </summary>
    [HttpGet("config/payment-settings")]
    public IActionResult GetPaymentSettings()
    {
        // Application assembly version is the easiest, no-PII build
        // identifier we have. Falls back to "(unknown)" when not set
        // (e.g. unattributed local builds).
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "(unknown)";
        var informationalVersion = Assembly
            .GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return Ok(new
        {
            mockCheckoutEnabled = _paymentSettings.Value.MockCheckoutEnabled,
            environmentName = _hostEnvironment.EnvironmentName,
            applicationName = _hostEnvironment.ApplicationName,
            assemblyVersion,
            informationalVersion,
            serverTimeUtc = DateTime.UtcNow,
        });
    }
}

using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SmartFuture.API.Controllers;

[ApiController]
[Route("api/diagnostics/ozow")]
public sealed class OzowDiagnosticsController : ControllerBase
{
    private const string DefaultOzowApiUrl = "https://api.ozow.com/PostPaymentRequest";
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public OzowDiagnosticsController(
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    [AllowAnonymous]
    [HttpGet("full-config-dump")]
    public IActionResult FullConfigDump()
    {
        return BuildResponse();
    }

    // Backup alias so this also works:
    // /api/payments/ozow/full-config-dump
    [AllowAnonymous]
    [HttpGet("/api/payments/ozow/full-config-dump")]
    public IActionResult FullConfigDumpPaymentsAlias()
    {
        return BuildResponse();
    }

    private IActionResult BuildResponse()
    {
        var enabled = ReadBool("Diagnostics__DumpFullOzowConfigOnStartup", "Diagnostics:DumpFullOzowConfigOnStartup");

        if (!enabled)
        {
            return NotFound("Emergency Ozow config dump is disabled. Set Diagnostics__DumpFullOzowConfigOnStartup=true and restart the API.");
        }

        var text = BuildDumpText();

        return Content(text, "text/plain", Encoding.UTF8);
    }

    private string BuildDumpText()
    {
        var sb = new StringBuilder();

        var ozowApiUrlRaw = Read("Ozow__ApiUrl", "Ozow:ApiUrl");
        var effectiveApiUrl = string.IsNullOrWhiteSpace(ozowApiUrlRaw)
            ? DefaultOzowApiUrl
            : ozowApiUrlRaw.Trim();

        var notifyUrl = Read("Ozow__NotifyUrl", "Ozow:NotifyUrl");
        var successUrl = Read("Ozow__SuccessUrl", "Ozow:SuccessUrl");
        var cancelUrl = Read("Ozow__CancelUrl", "Ozow:CancelUrl");
        var errorUrl = Read("Ozow__ErrorUrl", "Ozow:ErrorUrl");

        var enabledRaw = Read("Ozow__Enabled", "Ozow:Enabled");
        var isTestRaw = Read("Ozow__IsTest", "Ozow:IsTest");
        var useOverrideRaw = Read("Ozow__UseTestAmountOverride", "Ozow:UseTestAmountOverride");

        var corsOrigins = Enumerable.Range(0, 20)
            .Select(i => new
            {
                Index = i,
                Value = Read($"Cors__AllowedOrigins__{i}", $"Cors:AllowedOrigins:{i}")
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .ToList();

        var problems = new List<string>();

        if (!ParseBoolLoose(enabledRaw))
            problems.Add("Ozow__Enabled is not true.");

        if (string.IsNullOrWhiteSpace(Read("Ozow__SiteCode", "Ozow:SiteCode")))
            problems.Add("Ozow__SiteCode is EMPTY.");

        if (string.IsNullOrWhiteSpace(Read("Ozow__ApiKey", "Ozow:ApiKey")))
            problems.Add("Ozow__ApiKey is EMPTY.");

        if (string.IsNullOrWhiteSpace(Read("Ozow__PrivateKey", "Ozow:PrivateKey")))
            problems.Add("Ozow__PrivateKey is EMPTY.");

        if (string.IsNullOrWhiteSpace(notifyUrl))
            problems.Add("Ozow__NotifyUrl is EMPTY.");

        if (string.IsNullOrWhiteSpace(successUrl))
            problems.Add("Ozow__SuccessUrl is EMPTY.");

        if (string.IsNullOrWhiteSpace(cancelUrl))
            problems.Add("Ozow__CancelUrl is EMPTY.");

        if (string.IsNullOrWhiteSpace(errorUrl))
            problems.Add("Ozow__ErrorUrl is EMPTY.");

        if (!string.IsNullOrWhiteSpace(notifyUrl) &&
            !notifyUrl.Contains("/api/payments/ozow/notify", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("Ozow__NotifyUrl does not contain /api/payments/ozow/notify.");
        }

        var allUrls = new[] { notifyUrl, successUrl, cancelUrl, errorUrl }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var liveUsingUatUrls = allUrls.Any(x => x.Contains("uat", StringComparison.OrdinalIgnoreCase));
        var uatUsingLiveUrls = _environment.EnvironmentName.Contains("uat", StringComparison.OrdinalIgnoreCase)
            && allUrls.Any(x =>
                x.Contains("api.smartfuture.co.za", StringComparison.OrdinalIgnoreCase) ||
                x.Contains("clientzone.smartfuture.co.za", StringComparison.OrdinalIgnoreCase));

        if (_environment.IsProduction() && liveUsingUatUrls)
            problems.Add("LIVE/Production appears to be using UAT URLs.");

        if (uatUsingLiveUrls)
            problems.Add("UAT appears to be using LIVE URLs.");

        sb.AppendLine("================ EMERGENCY FULL OZOW CONFIG DUMP ================");
        sb.AppendLine($"Timestamp UTC: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"Environment: {_environment.EnvironmentName}");
        sb.AppendLine($"ASPNETCORE_ENVIRONMENT: {Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}");
        sb.AppendLine();

        sb.AppendLine($"Diagnostics__DumpFullOzowConfigOnStartup: {Read("Diagnostics__DumpFullOzowConfigOnStartup", "Diagnostics:DumpFullOzowConfigOnStartup")}");
        sb.AppendLine();

        sb.AppendLine($"Ozow__Enabled: {enabledRaw}");
        sb.AppendLine($"Ozow__Enabled parsed as: {ParseBoolLoose(enabledRaw)}");
        sb.AppendLine($"Ozow__SiteCode: {Read("Ozow__SiteCode", "Ozow:SiteCode")}");
        sb.AppendLine($"Ozow__ApiKey: {Read("Ozow__ApiKey", "Ozow:ApiKey")}");
        sb.AppendLine($"Ozow__PrivateKey: {Read("Ozow__PrivateKey", "Ozow:PrivateKey")}");
        sb.AppendLine($"Ozow__ApiUrl: {ozowApiUrlRaw}");
        sb.AppendLine($"Effective Ozow ApiUrl: {effectiveApiUrl}");
        sb.AppendLine($"Effective Ozow ApiUrl Source: {(string.IsNullOrWhiteSpace(ozowApiUrlRaw) ? "code default" : "env/config override")}");
        sb.AppendLine($"Ozow__NotifyUrl: {notifyUrl}");
        sb.AppendLine($"Ozow__SuccessUrl: {successUrl}");
        sb.AppendLine($"Ozow__CancelUrl: {cancelUrl}");
        sb.AppendLine($"Ozow__ErrorUrl: {errorUrl}");
        sb.AppendLine($"Ozow__IsTest: {isTestRaw}");
        sb.AppendLine($"Ozow__IsTest parsed as: {ParseBoolLoose(isTestRaw)}");
        sb.AppendLine($"Ozow__UseTestAmountOverride: {useOverrideRaw}");
        sb.AppendLine($"Ozow__UseTestAmountOverride parsed as: {ParseBoolLoose(useOverrideRaw)}");
        sb.AppendLine($"Ozow__TestAmount: {Read("Ozow__TestAmount", "Ozow:TestAmount")}");
        sb.AppendLine();

        sb.AppendLine($"NotifyUrl Host: {GetHost(notifyUrl)}");
        sb.AppendLine($"NotifyUrl Path: {GetPath(notifyUrl)}");
        sb.AppendLine($"NotifyUrl Looks Like API Notify Route: {notifyUrl?.Contains("/api/payments/ozow/notify", StringComparison.OrdinalIgnoreCase) == true}");
        sb.AppendLine($"SuccessUrl Host: {GetHost(successUrl)}");
        sb.AppendLine($"CancelUrl Host: {GetHost(cancelUrl)}");
        sb.AppendLine($"ErrorUrl Host: {GetHost(errorUrl)}");
        sb.AppendLine();

        sb.AppendLine($"Live using UAT URLs: {liveUsingUatUrls}");
        sb.AppendLine($"UAT using Live URLs: {uatUsingLiveUrls}");
        sb.AppendLine();

        sb.AppendLine($"PaymentSettings__MockCheckoutEnabled: {Read("PaymentSettings__MockCheckoutEnabled", "PaymentSettings:MockCheckoutEnabled")}");
        sb.AppendLine($"PaymentProcessing__WebhookApplyEnabled: {Read("PaymentProcessing__WebhookApplyEnabled", "PaymentProcessing:WebhookApplyEnabled")}");
        sb.AppendLine();

        sb.AppendLine("CORS Allowed Origins:");
        foreach (var origin in corsOrigins)
        {
            sb.AppendLine($"Cors__AllowedOrigins__{origin.Index}: {origin.Value}");
        }

        sb.AppendLine();

        sb.AppendLine($"Required Origin Present - https://clientzone.smartfuture.co.za: {corsOrigins.Any(x => string.Equals(x.Value?.TrimEnd('/'), "https://clientzone.smartfuture.co.za", StringComparison.OrdinalIgnoreCase))}");
        sb.AppendLine($"Required Origin Present - https://uat.portal.smartfuture.co.za: {corsOrigins.Any(x => string.Equals(x.Value?.TrimEnd('/'), "https://uat.portal.smartfuture.co.za", StringComparison.OrdinalIgnoreCase))}");
        sb.AppendLine();

        sb.AppendLine("Problems Detected:");
        if (problems.Count == 0)
        {
            sb.AppendLine("- None detected by config dump.");
        }
        else
        {
            foreach (var problem in problems)
            {
                sb.AppendLine($"- {problem}");
            }
        }

        sb.AppendLine("==================================================================");

        return sb.ToString();
    }

    private string? Read(string envKey, string configKey)
    {
        var envValue = Environment.GetEnvironmentVariable(envKey);

        if (!string.IsNullOrEmpty(envValue))
        {
            return envValue;
        }

        return _configuration[configKey];
    }

    private bool ReadBool(string envKey, string configKey)
    {
        return ParseBoolLoose(Read(envKey, configKey));
    }

    private static bool ParseBoolLoose(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim();

        if (bool.TryParse(normalized, out var parsed))
            return parsed;

        return normalized == "1" ||
               normalized.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("y", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "(empty)";

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host
            : "(invalid URL)";
    }

    private static string GetPath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "(empty)";

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : "(invalid URL)";
    }
}
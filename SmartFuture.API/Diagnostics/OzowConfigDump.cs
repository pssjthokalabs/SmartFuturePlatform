using SmartFuture.Application.Payments.Ozow;

namespace SmartFuture.API.Diagnostics;

/// <summary>
/// TEMPORARY EMERGENCY DIAGNOSTIC — full, UNMASKED Ozow configuration
/// dump, for comparing UAT against Live line by line.
///
/// ⚠ THIS PRINTS LIVE MERCHANT SECRETS (Ozow ApiKey and PrivateKey) IN
/// CLEARTEXT. It is OFF by default and only runs when
/// `Diagnostics:DumpFullOzowConfigOnStartup` is explicitly true.
///
/// Once the comparison is done: set the flag back to false, restart, and
/// treat any log file or console capture that contains a dump as
/// secret-bearing — rotate the Ozow ApiKey/PrivateKey if those logs are
/// retained, shipped off-box, or backed up.
///
/// Values are read STRAIGHT FROM <see cref="IConfiguration"/> so what is
/// printed is exactly what the API received, before binding coerced
/// anything. Each flag also reports how it PARSED, so a value like
/// "True " or "1" that binds differently than expected is visible.
/// </summary>
public static class OzowConfigDump
{
    /// <summary>Configuration key form (used to read the setting).</summary>
    public const string FlagKey = "Diagnostics:DumpFullOzowConfigOnStartup";

    /// <summary>Environment-variable form, for operator-facing messages.</summary>
    public const string FlagEnvVar = "Diagnostics__DumpFullOzowConfigOnStartup";

    /// <summary>Max index probed for Cors:AllowedOrigins:{n}.</summary>
    private const int CorsOriginSlots = 10;

    private const string LiveClientZone = "https://clientzone.smartfuture.co.za";
    private const string UatPortal      = "https://uat.portal.smartfuture.co.za";
    private const string LiveApiHost    = "api.smartfuture.co.za";
    private const string UatApiHost     = "uatapi.smartfuture.co.za";
    private const string NotifyPath     = "/api/payments/ozow/notify";

    public static bool IsEnabled(IConfiguration configuration)
        => configuration.GetValue<bool?>(FlagKey) ?? false;

    /// <summary>
    /// Build the dump model. Shared by the startup printer and the
    /// admin endpoint so both report identical values.
    /// </summary>
    public static OzowConfigDumpModel Build(IConfiguration configuration, IHostEnvironment env)
    {
        string Raw(string key) => configuration[key] ?? string.Empty;

        var enabledRaw      = Raw("Ozow:Enabled");
        var siteCode        = Raw("Ozow:SiteCode");
        var apiKey          = Raw("Ozow:ApiKey");
        var privateKey      = Raw("Ozow:PrivateKey");
        var apiUrlRaw       = Raw("Ozow:ApiUrl");
        var notifyUrl       = Raw("Ozow:NotifyUrl");
        var successUrl      = Raw("Ozow:SuccessUrl");
        var cancelUrl       = Raw("Ozow:CancelUrl");
        var errorUrl        = Raw("Ozow:ErrorUrl");
        var isTestRaw       = Raw("Ozow:IsTest");
        var overrideRaw     = Raw("Ozow:UseTestAmountOverride");
        var testAmountRaw   = Raw("Ozow:TestAmount");

        var apiUrlOverridden = !string.IsNullOrWhiteSpace(apiUrlRaw);
        var effectiveApiUrl  = apiUrlOverridden ? apiUrlRaw : OzowSettings.DefaultApiUrl;

        var origins = new List<string>();
        for (var i = 0; i < CorsOriginSlots; i++)
            origins.Add(Raw($"Cors:AllowedOrigins:{i}"));

        var isProduction = env.IsProduction();
        var notifyHost   = HostOf(notifyUrl);
        var notifyPath   = PathOf(notifyUrl);

        // Cross-environment mix-up detection. The single most likely
        // cause of "works on UAT, dies on Live" is a half-copied env var
        // block, so call it out explicitly rather than making someone
        // eyeball two long lists.
        var allUrls = new[] { notifyUrl, successUrl, cancelUrl, errorUrl };
        var referencesUat  = allUrls.Any(u => !string.IsNullOrWhiteSpace(u)
                                && (u.Contains(UatApiHost, StringComparison.OrdinalIgnoreCase)
                                 || u.Contains("uat.", StringComparison.OrdinalIgnoreCase)));
        var referencesLive = allUrls.Any(u => !string.IsNullOrWhiteSpace(u)
                                && (u.Contains(LiveApiHost, StringComparison.OrdinalIgnoreCase)
                                 || u.Contains(LiveClientZone, StringComparison.OrdinalIgnoreCase)));

        var model = new OzowConfigDumpModel
        {
            EnvironmentName = env.EnvironmentName,
            AspNetCoreEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "(unset)",

            EnabledRaw = enabledRaw,
            SiteCode = siteCode,
            ApiKey = apiKey,
            PrivateKey = privateKey,
            ApiUrlRaw = apiUrlRaw,
            EffectiveApiUrl = effectiveApiUrl,
            ApiUrlSource = apiUrlOverridden ? "env var (Ozow__ApiUrl)" : "code default (OzowSettings.DefaultApiUrl)",
            NotifyUrl = notifyUrl,
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            ErrorUrl = errorUrl,
            IsTestRaw = isTestRaw,
            UseTestAmountOverrideRaw = overrideRaw,
            TestAmountRaw = testAmountRaw,

            EnabledParsed = ParseBool(enabledRaw),
            IsTestParsed = ParseBool(isTestRaw),
            UseTestAmountOverrideParsed = ParseBool(overrideRaw),

            NotifyUrlHost = notifyHost,
            NotifyUrlPath = notifyPath,
            NotifyUrlLooksLikeApiNotifyRoute =
                notifyUrl.Contains(NotifyPath, StringComparison.OrdinalIgnoreCase),
            SuccessUrlHost = HostOf(successUrl),
            CancelUrlHost = HostOf(cancelUrl),
            ErrorUrlHost = HostOf(errorUrl),

            MockCheckoutEnabledRaw = Raw("PaymentSettings:MockCheckoutEnabled"),
            WebhookApplyEnabledRaw = Raw("PaymentProcessing:WebhookApplyEnabled"),

            CorsAllowedOrigins = origins,
            LiveClientZoneOriginPresent = origins.Any(o => Same(o, LiveClientZone)),
            UatPortalOriginPresent = origins.Any(o => Same(o, UatPortal)),

            IsProduction = isProduction,
            LiveUsingUatUrls = isProduction && referencesUat,
            UatUsingLiveUrls = !isProduction && referencesLive,
        };

        model.Problems = DetectProblems(model);
        return model;
    }

    private static List<string> DetectProblems(OzowConfigDumpModel m)
    {
        var p = new List<string>();

        if (m.EnabledParsed != true)
            p.Add($"Ozow__Enabled did not parse as true (raw: '{Show(m.EnabledRaw)}'). Customer-facing Ozow initiation is refused.");
        if (string.IsNullOrWhiteSpace(m.SiteCode))     p.Add("Ozow__SiteCode is EMPTY.");
        if (string.IsNullOrWhiteSpace(m.ApiKey))       p.Add("Ozow__ApiKey is EMPTY.");
        if (string.IsNullOrWhiteSpace(m.PrivateKey))   p.Add("Ozow__PrivateKey is EMPTY.");
        if (m.IsTestParsed is null)
            p.Add($"Ozow__IsTest did not parse as a bool (raw: '{Show(m.IsTestRaw)}'). It must be explicitly true or false.");
        if (string.IsNullOrWhiteSpace(m.NotifyUrl))
            p.Add("Ozow__NotifyUrl is EMPTY — an order-intent payment could never convert to an order.");
        else if (!m.NotifyUrlLooksLikeApiNotifyRoute)
            p.Add($"Ozow__NotifyUrl does not contain '{NotifyPath}' (value: {m.NotifyUrl}). Ozow's notification will not reach the handler.");
        if (string.IsNullOrWhiteSpace(m.SuccessUrl))   p.Add("Ozow__SuccessUrl is EMPTY.");
        if (string.IsNullOrWhiteSpace(m.CancelUrl))    p.Add("Ozow__CancelUrl is EMPTY.");
        if (string.IsNullOrWhiteSpace(m.ErrorUrl))     p.Add("Ozow__ErrorUrl is EMPTY (falls back to CancelUrl).");

        if (m.LiveUsingUatUrls)
            p.Add("LIVE is referencing UAT hosts in one or more Ozow URLs — check for a half-copied env-var block.");
        if (m.UatUsingLiveUrls)
            p.Add("UAT is referencing LIVE hosts in one or more Ozow URLs — a UAT payment could settle against production.");

        if (m.IsProduction && m.IsTestParsed == true)
            p.Add("Production is running with Ozow__IsTest=true — real customers would hit Ozow's test rails.");
        if (m.IsProduction && m.UseTestAmountOverrideParsed == true)
            p.Add("Ozow__UseTestAmountOverride=true in Production. The code force-disables it there, but the flag should be removed.");

        if (m.NotifyUrlHost.Equals(HostOf(m.SuccessUrl), StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(m.NotifyUrlHost))
            p.Add($"NotifyUrl and SuccessUrl share host '{m.NotifyUrlHost}'. NotifyUrl must point at the API; Success/Cancel/Error at the PORTAL.");

        if (m.ApiUrlIsStagingLike)
            p.Add($"Ozow__ApiUrl looks like a staging endpoint ({m.ApiUrlRaw}). Live credentials fail against staging with 'merchant not found'.");

        return p;
    }

    /// <summary>Render the exact fixed-format block for stdout.</summary>
    public static string Render(OzowConfigDumpModel m)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("================ EMERGENCY FULL OZOW CONFIG DUMP ================");
        sb.AppendLine("*** WARNING: contains LIVE SECRETS in cleartext. Disable");
        sb.AppendLine("*** Diagnostics__DumpFullOzowConfigOnStartup when finished, and");
        sb.AppendLine("*** rotate the Ozow ApiKey/PrivateKey if these logs are retained.");
        sb.AppendLine($"Environment: {m.EnvironmentName}");
        sb.AppendLine($"ASPNETCORE_ENVIRONMENT: {m.AspNetCoreEnvironment}");
        sb.AppendLine();
        sb.AppendLine($"Ozow__Enabled: {Show(m.EnabledRaw)}");
        sb.AppendLine($"Ozow__SiteCode: {Show(m.SiteCode)}");
        sb.AppendLine($"Ozow__ApiKey: {Show(m.ApiKey)}");
        sb.AppendLine($"Ozow__PrivateKey: {Show(m.PrivateKey)}");
        sb.AppendLine($"Ozow__ApiUrl: {Show(m.ApiUrlRaw)}");
        sb.AppendLine($"Effective Ozow ApiUrl: {m.EffectiveApiUrl}   [source: {m.ApiUrlSource}]");
        sb.AppendLine($"Ozow__NotifyUrl: {Show(m.NotifyUrl)}");
        sb.AppendLine($"Ozow__SuccessUrl: {Show(m.SuccessUrl)}");
        sb.AppendLine($"Ozow__CancelUrl: {Show(m.CancelUrl)}");
        sb.AppendLine($"Ozow__ErrorUrl: {Show(m.ErrorUrl)}");
        sb.AppendLine($"Ozow__IsTest: {Show(m.IsTestRaw)}");
        sb.AppendLine($"Ozow__UseTestAmountOverride: {Show(m.UseTestAmountOverrideRaw)}");
        sb.AppendLine($"Ozow__TestAmount: {Show(m.TestAmountRaw)}");
        sb.AppendLine();
        sb.AppendLine($"Enabled parsed as: {ShowBool(m.EnabledParsed)}");
        sb.AppendLine($"IsTest parsed as: {ShowBool(m.IsTestParsed)}");
        sb.AppendLine($"UseTestAmountOverride parsed as: {ShowBool(m.UseTestAmountOverrideParsed)}");
        sb.AppendLine();
        sb.AppendLine($"NotifyUrl Host: {Show(m.NotifyUrlHost)}");
        sb.AppendLine($"NotifyUrl Path: {Show(m.NotifyUrlPath)}");
        sb.AppendLine($"NotifyUrl Looks Like API Notify Route: {m.NotifyUrlLooksLikeApiNotifyRoute}");
        sb.AppendLine($"SuccessUrl Host: {Show(m.SuccessUrlHost)}");
        sb.AppendLine($"CancelUrl Host: {Show(m.CancelUrlHost)}");
        sb.AppendLine($"ErrorUrl Host: {Show(m.ErrorUrlHost)}");
        sb.AppendLine();
        sb.AppendLine($"PaymentSettings__MockCheckoutEnabled: {Show(m.MockCheckoutEnabledRaw)}");
        sb.AppendLine($"PaymentProcessing__WebhookApplyEnabled: {Show(m.WebhookApplyEnabledRaw)}");
        sb.AppendLine();
        sb.AppendLine("CORS Allowed Origins:");
        for (var i = 0; i < m.CorsAllowedOrigins.Count; i++)
            sb.AppendLine($"Cors__AllowedOrigins__{i}: {Show(m.CorsAllowedOrigins[i])}");
        sb.AppendLine();
        sb.AppendLine("NOTE: the active CORS policy is SetIsOriginAllowed(_ => true) —");
        sb.AppendLine("every origin is permitted regardless of the list above, so an");
        sb.AppendLine("empty list here does NOT mean CORS is misconfigured.");
        sb.AppendLine();
        sb.AppendLine("Required Origin Present:");
        sb.AppendLine($"{LiveClientZone} present: {m.LiveClientZoneOriginPresent}");
        sb.AppendLine($"{UatPortal} present: {m.UatPortalOriginPresent}");
        sb.AppendLine();
        sb.AppendLine($"Live using UAT URLs: {m.LiveUsingUatUrls}");
        sb.AppendLine($"UAT using Live URLs: {m.UatUsingLiveUrls}");
        sb.AppendLine();
        sb.AppendLine("Problems Detected:");
        if (m.Problems.Count == 0) sb.AppendLine("- (none)");
        else foreach (var problem in m.Problems) sb.AppendLine($"- {problem}");
        sb.AppendLine("==================================================================");
        return sb.ToString();
    }

    private static string Show(string? value)
        => string.IsNullOrEmpty(value) ? "(EMPTY)" : value;

    private static string ShowBool(bool? value)
        => value.HasValue ? value.Value.ToString().ToLowerInvariant() : "(UNPARSEABLE)";

    // Deliberately tolerant of the shapes people actually type into a
    // hosting control panel.
    private static bool? ParseBool(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        if (bool.TryParse(t, out var b)) return b;
        return t switch
        {
            "1" or "yes" or "Yes" or "YES" or "on"  or "On"  or "ON"  => true,
            "0" or "no"  or "No"  or "NO"  or "off" or "Off" or "OFF" => false,
            _ => null
        };
    }

    private static string HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "(unparseable)";
    }

    private static string PathOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : "(unparseable)";
    }

    private static bool Same(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a)
        && string.Equals(a.Trim().TrimEnd('/'), b?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Full unmasked Ozow config snapshot. Serialised as-is by the
/// admin endpoint; rendered as text by the startup dump.</summary>
public class OzowConfigDumpModel
{
    public string EnvironmentName { get; set; } = string.Empty;
    public string AspNetCoreEnvironment { get; set; } = string.Empty;

    // Raw values, exactly as IConfiguration received them.
    public string EnabledRaw { get; set; } = string.Empty;
    public string SiteCode { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string PrivateKey { get; set; } = string.Empty;
    public string ApiUrlRaw { get; set; } = string.Empty;
    public string EffectiveApiUrl { get; set; } = string.Empty;
    public string ApiUrlSource { get; set; } = string.Empty;
    public string NotifyUrl { get; set; } = string.Empty;
    public string SuccessUrl { get; set; } = string.Empty;
    public string CancelUrl { get; set; } = string.Empty;
    public string ErrorUrl { get; set; } = string.Empty;
    public string IsTestRaw { get; set; } = string.Empty;
    public string UseTestAmountOverrideRaw { get; set; } = string.Empty;
    public string TestAmountRaw { get; set; } = string.Empty;

    // How the flags actually parsed. Null = unparseable.
    public bool? EnabledParsed { get; set; }
    public bool? IsTestParsed { get; set; }
    public bool? UseTestAmountOverrideParsed { get; set; }

    public string NotifyUrlHost { get; set; } = string.Empty;
    public string NotifyUrlPath { get; set; } = string.Empty;
    public bool NotifyUrlLooksLikeApiNotifyRoute { get; set; }
    public string SuccessUrlHost { get; set; } = string.Empty;
    public string CancelUrlHost { get; set; } = string.Empty;
    public string ErrorUrlHost { get; set; } = string.Empty;

    public string MockCheckoutEnabledRaw { get; set; } = string.Empty;
    public string WebhookApplyEnabledRaw { get; set; } = string.Empty;

    public List<string> CorsAllowedOrigins { get; set; } = new();
    public bool LiveClientZoneOriginPresent { get; set; }
    public bool UatPortalOriginPresent { get; set; }

    public bool IsProduction { get; set; }
    public bool LiveUsingUatUrls { get; set; }
    public bool UatUsingLiveUrls { get; set; }

    public List<string> Problems { get; set; } = new();

    /// <summary>True when a non-default ApiUrl looks like Ozow staging.</summary>
    public bool ApiUrlIsStagingLike =>
        !string.IsNullOrWhiteSpace(ApiUrlRaw)
        && ApiUrlRaw.Contains("stag", StringComparison.OrdinalIgnoreCase);
}

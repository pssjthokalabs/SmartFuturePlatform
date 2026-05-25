namespace SmartFuture.Application.Coverage;

// Runtime configuration for the public Coverage check endpoint.
//
//   CoverageSettings__Openserve__BaseUrl
//   CoverageSettings__Openserve__TimeoutSeconds
//   CoverageSettings__GoogleMaps__ApiKey   (optional)
//   CoverageSettings__GoogleMaps__Region   (optional, e.g. "za")
//
// Defaults aim at "works out-of-the-box on UAT" — Openserve's public
// GIS endpoint is unauthenticated so the BaseUrl default is safe, and
// the GoogleMaps key stays empty so production must opt in explicitly.
// When the GoogleMaps key is blank the geocoding service returns
// PROVIDER_NOT_CONFIGURED — callers must supply lat/lon directly until
// a key is provisioned.
public class CoverageSettings
{
    public const string SectionName = "CoverageSettings";

    public OpenserveSettings  Openserve  { get; set; } = new();
    public GoogleMapsSettings GoogleMaps { get; set; } = new();
}

public class OpenserveSettings
{
    public string BaseUrl        { get; set; } = "https://apps.openserve.co.za";
    public int    TimeoutSeconds { get; set; } = 10;
}

public class GoogleMapsSettings
{
    public string  ApiKey { get; set; } = string.Empty;
    public string? Region { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

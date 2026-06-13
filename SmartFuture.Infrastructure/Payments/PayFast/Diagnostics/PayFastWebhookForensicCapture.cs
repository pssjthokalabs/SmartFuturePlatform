using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments.PayFast.Diagnostics;

namespace SmartFuture.Infrastructure.Payments.PayFast.Diagnostics;

/// <summary>
/// TEMPORARY UAT-ONLY forensic JSON writer for PayFast ITNs. See
/// <see cref="PaymentDiagnosticsOptions"/> for the toggles. Remove or
/// hard-gate to non-Production before going live.
/// </summary>
public class PayFastWebhookForensicCapture : IPayFastWebhookForensicCapture
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    private readonly PaymentDiagnosticsOptions _options;
    private readonly IHostEnvironment _env;
    private readonly ILogger<PayFastWebhookForensicCapture> _logger;

    public PayFastWebhookForensicCapture(
        IOptions<PaymentDiagnosticsOptions> options,
        IHostEnvironment env,
        ILogger<PayFastWebhookForensicCapture> logger)
    {
        _options = options.Value;
        _env = env;
        _logger = logger;
    }

    public bool IsEnabled => _options.CapturePayFastWebhook;

    public async Task<string?> TryWriteAsync(
        PayFastForensicSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled) return null;
        if (snapshot is null) return null;

        try
        {
            var basePath = ResolvePath(_options.WebhookLogPath);
            Directory.CreateDirectory(basePath);

            var fileName = $"payfast-itn-{snapshot.CapturedAtUtc:yyyyMMdd-HHmmss}-{snapshot.DiagId}.json";
            var fullPath = Path.Combine(basePath, fileName);

            await using var stream = File.Create(fullPath);
            await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);

            snapshot.WrittenFilePath = fullPath;
            return fullPath;
        }
        catch (Exception ex)
        {
            // Never fail the webhook because the disk is full / read-only /
            // permission-denied. Log loudly so the operator notices.
            _logger.LogError(ex,
                "[payment][payfast][forensic_file_error] diagId={DiagId} basePath={BasePath} message={Message}",
                snapshot.DiagId, _options.WebhookLogPath, ex.Message);
            return null;
        }
    }

    private string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) path = "logs/webhooks";
        return Path.IsPathRooted(path) ? path : Path.Combine(_env.ContentRootPath, path);
    }
}

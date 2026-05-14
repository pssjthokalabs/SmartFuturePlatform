using Microsoft.Extensions.Logging;
using SmartFuture.Application.Webhooks;
using SmartFuture.Application.Webhooks.Dtos;
using SmartFuture.Shared.Enums.Webhooks;

namespace SmartFuture.Infrastructure.Webhooks;

/// <summary>
/// Default validator for Phase 9B scaffolding. Accepts only the explicit "Manual" provider.
/// Real provider validators (PayFast/Peach/Paystack/Yoco/Ozow) must replace this DI registration
/// before any non-manual webhook can be processed in production.
/// </summary>
public class PermissiveWebhookSignatureValidator : IWebhookSignatureValidator
{
    private readonly ILogger<PermissiveWebhookSignatureValidator> _logger;

    public PermissiveWebhookSignatureValidator(ILogger<PermissiveWebhookSignatureValidator> logger)
    {
        _logger = logger;
    }

    public Task<bool> IsValidAsync(PaymentWebhookRequestDto request, CancellationToken cancellationToken = default)
    {
        var isManual = request.Provider == WebhookProvider.Manual
                    || string.Equals(request.ProviderName, "Manual", StringComparison.OrdinalIgnoreCase);

        if (isManual)
        {
            _logger.LogWarning(
                "PermissiveWebhookSignatureValidator accepting Manual webhook for provider '{Provider}'. " +
                "This validator is for development/admin reconciliation only.",
                request.ProviderName);
            return Task.FromResult(true);
        }

        _logger.LogWarning(
            "PermissiveWebhookSignatureValidator rejecting webhook for provider '{Provider}' " +
            "because no real signature validator is configured. " +
            "Register a provider-specific IWebhookSignatureValidator before going live.",
            request.ProviderName);

        return Task.FromResult(false);
    }
}

using Microsoft.Extensions.Logging;
using SmartFuture.Application.Webhooks;
using SmartFuture.Application.Webhooks.Dtos;
using SmartFuture.Shared.Enums.Webhooks;

namespace SmartFuture.Infrastructure.Webhooks;

/// <summary>
/// Aggregates per-provider <see cref="IWebhookSignatureValidator"/> implementations into a
/// single dispatch point. For Manual webhooks falls back to the permissive validator that
/// ships with Phase 9B. For real providers (PayFast/Peach/etc.), reject until a provider-
/// specific validator is registered — production deployments must add real validators
/// before processing any non-Manual webhook traffic.
/// </summary>
public class CompositeWebhookSignatureValidator : IWebhookSignatureValidator
{
    private readonly Dictionary<WebhookProvider, IWebhookSignatureValidator> _providerValidators;
    private readonly IWebhookSignatureValidator _manualFallback;
    private readonly ILogger<CompositeWebhookSignatureValidator> _logger;

    public CompositeWebhookSignatureValidator(IEnumerable<IProviderSignatureValidator> providerValidators, PermissiveWebhookSignatureValidator manualFallback, ILogger<CompositeWebhookSignatureValidator> logger)
    {
        _providerValidators = providerValidators.ToDictionary(v => v.Provider, v => (IWebhookSignatureValidator)v);
        _manualFallback = manualFallback;
        _logger = logger;
    }

    public Task<bool> IsValidAsync(PaymentWebhookRequestDto request, CancellationToken cancellationToken = default)
    {
        if (request.Provider == WebhookProvider.Manual
            || string.Equals(request.ProviderName, "Manual", StringComparison.OrdinalIgnoreCase))
        {
            return _manualFallback.IsValidAsync(request, cancellationToken);
        }

        if (_providerValidators.TryGetValue(request.Provider, out var validator))
            return validator.IsValidAsync(request, cancellationToken);

        _logger.LogWarning(
            "No provider-specific webhook signature validator registered for '{Provider}'. " +
            "Register an IProviderSignatureValidator implementation in DI before processing " +
            "non-Manual webhook traffic.",
            request.ProviderName);

        return Task.FromResult(false);
    }
}

/// <summary>
/// Marker interface for per-provider signature validators. Implementations should be
/// registered in DI alongside <see cref="CompositeWebhookSignatureValidator"/>.
/// </summary>
public interface IProviderSignatureValidator : IWebhookSignatureValidator
{
    WebhookProvider Provider { get; }
}

using Microsoft.Extensions.Logging;
using SmartFuture.Application.Notifications;

namespace SmartFuture.API.HostedServices;

/// <summary>
/// One-shot startup logger that resolves the registered
/// <see cref="INotificationSender"/> and prints its concrete type
/// name. Removes any ambiguity about which sender DI actually wired
/// up — the most common cause of "we set up SMTP but emails don't
/// go out" is the logging sender being registered because
/// <c>EmailSettings:Provider</c> wasn't recognised at boot.
///
/// Logged once at host start, never again. No secrets, no
/// per-message data.
/// </summary>
public sealed class EmailSenderStartupLogger : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<EmailSenderStartupLogger> _logger;

    public EmailSenderStartupLogger(IServiceProvider services, ILogger<EmailSenderStartupLogger> logger)
    {
        _services = services;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // INotificationSender is registered as Scoped — open a
            // throw-away scope just to resolve it and report the type.
            using var scope = _services.CreateScope();
            var sender = scope.ServiceProvider.GetService<INotificationSender>();
            if (sender is null)
            {
                _logger.LogWarning("No INotificationSender is registered. Outbound emails will not be dispatched.");
            }
            else
            {
                _logger.LogInformation(
                    "INotificationSender resolved at startup: {SenderType}",
                    sender.GetType().FullName);
            }
        }
        catch (Exception ex)
        {
            // Diagnostic must never break startup.
            _logger.LogError(ex, "Failed to resolve INotificationSender at startup for diagnostics.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

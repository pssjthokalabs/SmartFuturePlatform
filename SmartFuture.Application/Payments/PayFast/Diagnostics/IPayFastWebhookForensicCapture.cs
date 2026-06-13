namespace SmartFuture.Application.Payments.PayFast.Diagnostics;

/// <summary>
/// TEMPORARY UAT-ONLY forensic file writer. The controller checks
/// <see cref="IsEnabled"/> at the very start of each PayFast webhook
/// hit. When false, no allocation / file I/O is performed and the
/// controller behaves exactly as it does in production.
/// </summary>
public interface IPayFastWebhookForensicCapture
{
    bool IsEnabled { get; }

    /// <summary>Best-effort write. Catches and logs any exception via
    /// ILogger so a disk / permission issue cannot break the webhook
    /// pipeline. Returns the file path on success, null on failure
    /// (or when disabled).</summary>
    Task<string?> TryWriteAsync(
        PayFastForensicSnapshot snapshot,
        CancellationToken cancellationToken = default);
}

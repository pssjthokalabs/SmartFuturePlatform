using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.Ozow;

/// <summary>
/// Phase 52 — handles Ozow's POST-back webhook payload.
///
/// Lives in Application (no transport coupling) so the controller is a
/// thin AllowAnonymous shell that just collects the form payload and
/// hands it here. Idempotent: replaying the same webhook is a no-op
/// once the payment is already Completed.
/// </summary>
public class OzowNotifyHandler
{
    private readonly IAppDbContext _dbContext;
    private readonly IPaymentApplierService _applier;
    private readonly OzowSettings _settings;
    private readonly ILogger<OzowNotifyHandler> _logger;

    public OzowNotifyHandler(IAppDbContext dbContext, IPaymentApplierService applier, IOptions<OzowSettings> settings, ILogger<OzowNotifyHandler> logger)
    {
        _dbContext = dbContext;
        _applier = applier;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<OzowNotifyOutcome> HandleAsync(OzowNotifyPayload payload, CancellationToken cancellationToken)
    {
        if (payload is null) return new OzowNotifyOutcome(false, "Empty payload");

        if (!_settings.IsConfigured)
        {
            _logger.LogWarning("Received Ozow notify but Ozow is not configured — ignoring.");
            return new OzowNotifyOutcome(false, "Ozow not configured");
        }

        // 1. Verify the hash. Constant-time compare lives in the
        //    calculator. If this fails we *do not* reveal which field
        //    was wrong — just log + return 200 OK so Ozow doesn't
        //    retry forever (per their spec).
        var expectedHash = OzowHashCalculator.BuildResponseHash(
            siteCode:             payload.SiteCode             ?? string.Empty,
            transactionId:        payload.TransactionId        ?? string.Empty,
            transactionReference: payload.TransactionReference ?? string.Empty,
            amount:               payload.Amount,
            status:               payload.Status               ?? string.Empty,
            optional1:            payload.Optional1,
            optional2:            payload.Optional2,
            optional3:            payload.Optional3,
            optional4:            payload.Optional4,
            optional5:            payload.Optional5,
            currencyCode:         payload.CurrencyCode         ?? _settings.CurrencyCode,
            isTest:               payload.IsTest,
            statusMessage:        payload.StatusMessage,
            privateKey:           _settings.PrivateKey);

        if (!OzowHashCalculator.HashesMatch(expectedHash, payload.Hash))
        {
            _logger.LogWarning(
                "Ozow notify hash mismatch for reference {Reference} (TransactionId {TransactionId})",
                payload.TransactionReference, payload.TransactionId);
            return new OzowNotifyOutcome(false, "Hash mismatch");
        }

        if (string.IsNullOrWhiteSpace(payload.TransactionReference))
            return new OzowNotifyOutcome(false, "Missing TransactionReference");

        // 2. Find the matching PaymentInitiation by TransactionReference
        //    (we set it on initiate). Includes the Payment + Invoice so
        //    we have everything for the apply step + amount cross-check.
        var initiation = await _dbContext.PaymentInitiations
            .Include(i => i.Payment)
            .Include(i => i.Invoice)
            .FirstOrDefaultAsync(i => i.Provider == PaymentProviderType.Ozow
                                   && i.ProviderReference == payload.TransactionReference,
                                 cancellationToken);

        if (initiation is null || initiation.Payment is null || initiation.Invoice is null)
        {
            _logger.LogWarning("Ozow notify for unknown reference {Reference}", payload.TransactionReference);
            return new OzowNotifyOutcome(false, "Unknown reference");
        }

        // 3. Amount sanity-check — server-recorded payment.Amount must
        //    match what Ozow says was charged. Mismatch is treated as a
        //    fraud / replay attempt: log + bail without flipping status.
        if (Math.Abs(initiation.Payment.Amount - payload.Amount) > 0.01m)
        {
            _logger.LogWarning(
                "Ozow notify amount mismatch for {Reference}: expected {Expected}, got {Got}",
                payload.TransactionReference, initiation.Payment.Amount, payload.Amount);
            return new OzowNotifyOutcome(false, "Amount mismatch");
        }

        // 4. Persist the Ozow TransactionId onto the payment +
        //    initiation. Safe to re-write — same value every time the
        //    same notification fires.
        if (!string.IsNullOrWhiteSpace(payload.TransactionId))
        {
            initiation.Payment.GatewayTransactionId = payload.TransactionId;
            initiation.ProviderCheckoutId = payload.TransactionId;
        }

        // 5. Map Ozow status → PaymentStatus and let
        //    PaymentApplierService own the canonical transition. The
        //    applier is itself idempotent (no-ops when the payment is
        //    already in the requested state) so we can replay this
        //    notification safely.
        var mapped = MapOzowStatus(payload.Status);
        if (!mapped.HasValue)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Ozow notify for {Reference} with non-terminal status '{Status}' — stored but no state change.",
                payload.TransactionReference, payload.Status);
            return new OzowNotifyOutcome(true, $"Stored. No state change for status '{payload.Status}'.");
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var result = await _applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId            = initiation.Payment.Id,
            NewStatus            = mapped.Value,
            FailureReason        = mapped.Value == PaymentStatus.Failed ? payload.StatusMessage : null,
            TriggerNotifications = true
        }, cancellationToken);

        if (!result.IsSuccess)
        {
            _logger.LogError(
                "ApplyStatusChangeAsync failed for Ozow notify {Reference}: {Code} {Message}",
                payload.TransactionReference, result.Code, result.Message);
            return new OzowNotifyOutcome(false, result.Message ?? "Apply failed");
        }

        return new OzowNotifyOutcome(true, $"Payment {initiation.Payment.PaymentNumber} updated to {mapped.Value}.");
    }

    private static PaymentStatus? MapOzowStatus(string? ozowStatus)
    {
        if (string.IsNullOrWhiteSpace(ozowStatus)) return null;
        return ozowStatus.Trim().ToLowerInvariant() switch
        {
            "complete"             => PaymentStatus.Completed,
            "cancelled"            => PaymentStatus.Failed,    // customer-cancelled = Failed bucket; FailureReason captures why
            "abandoned"            => PaymentStatus.Failed,
            "error"                => PaymentStatus.Failed,
            "pendinginvestigation" => null,                    // non-terminal, leave the row alone
            _                      => null
        };
    }
}

public class OzowNotifyOutcome
{
    public bool   Accepted { get; }
    public string Message  { get; }
    public OzowNotifyOutcome(bool accepted, string message)
    {
        Accepted = accepted;
        Message = message;
    }
}

/// <summary>
/// Ozow's notify payload — Ozow POSTs it as
/// <c>application/x-www-form-urlencoded</c>. Field names + casing
/// match Ozow's docs so model binding picks them up directly from the
/// form.
/// </summary>
public class OzowNotifyPayload
{
    public string? SiteCode             { get; set; }
    public string? TransactionId        { get; set; }
    public string? TransactionReference { get; set; }
    public decimal Amount               { get; set; }
    public string? Status               { get; set; }
    public string? Optional1            { get; set; }
    public string? Optional2            { get; set; }
    public string? Optional3            { get; set; }
    public string? Optional4            { get; set; }
    public string? Optional5            { get; set; }
    public string? CurrencyCode         { get; set; }
    public bool    IsTest               { get; set; }
    public string? StatusMessage        { get; set; }
    public string? Hash                 { get; set; }
}

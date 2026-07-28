using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.OrderIntents;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.OrderIntents;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.Ozow;

/// <summary>
/// Phase 52 — handles Ozow's POST-back webhook payload.
///
/// Lives in Application (no transport coupling) so the controller is a
/// thin AllowAnonymous shell that just collects the form payload and
/// hands it here. Idempotent: replaying the same webhook is a no-op
/// once the payment is already Completed.
///
/// TWO settlement paths, routed by TransactionReference prefix:
///
///   • "SF-INTENT-…"  → NEW-ORDER intent. Nothing exists yet but an
///     OrderIntent; a Complete status calls
///     <c>ConvertIntentPaymentToPaidOrderAsync</c>, which atomically
///     mints Order + Invoice (Paid) + Payment (Completed) + Pending
///     NetworkAccount. This is the ONLY way an Ozow new-order payment
///     becomes an order — there is no client-side verify fallback the
///     way Paystack has one.
///
///   • "SF-{paymentNumber}" → INVOICE payment. Invoice + Payment +
///     PaymentInitiation already exist; PaymentApplierService owns the
///     status transition.
///
/// The prefix contract is enforced at initiate time on both sides
/// (see OzowIntentInitiationService / OzowPaymentInitiator) so a
/// reference can never be ambiguous.
/// </summary>
public class OzowNotifyHandler
{
    private const string IntentReferencePrefix = "SF-INTENT-";

    private readonly IAppDbContext _dbContext;
    private readonly IPaymentApplierService _applier;
    private readonly IOrderIntentService _orderIntentService;
    private readonly OzowSettings _settings;
    private readonly ILogger<OzowNotifyHandler> _logger;

    public OzowNotifyHandler(
        IAppDbContext dbContext,
        IPaymentApplierService applier,
        IOrderIntentService orderIntentService,
        IOptions<OzowSettings> settings,
        ILogger<OzowNotifyHandler> logger)
    {
        _dbContext = dbContext;
        _applier = applier;
        _orderIntentService = orderIntentService;
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

        // 1b. Route by reference prefix. Intent payments have no
        //     PaymentInitiation / Payment / Invoice to find, so they must
        //     be handled before the invoice lookup below (which would
        //     otherwise log "unknown reference" and drop a paid order on
        //     the floor).
        if (payload.TransactionReference.StartsWith(IntentReferencePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return await HandleIntentNotifyAsync(payload, cancellationToken);
        }

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

    /// <summary>
    /// NEW-ORDER intent settlement. The hash has already been verified by
    /// the caller, so the payload is trusted at this point.
    ///
    /// This is the only path that turns an Ozow new-order payment into an
    /// actual Order. Every early return therefore logs an
    /// [OzowIntentReconcile] line: if a customer is charged and no order
    /// appears, that tag plus the reference is the whole audit trail.
    /// </summary>
    private async Task<OzowNotifyOutcome> HandleIntentNotifyAsync(
        OzowNotifyPayload payload,
        CancellationToken cancellationToken)
    {
        var reference = payload.TransactionReference!.Trim();

        var intent = await _dbContext.OrderIntents
            .FirstOrDefaultAsync(i => i.IntentPaymentReference == reference, cancellationToken);

        if (intent is null)
        {
            // Hash was valid, so Ozow really did mint this against our
            // site code — but we have no intent for it. Either the intent
            // row was deleted, or the reference was minted by another
            // environment sharing the same Ozow credentials (a real
            // hazard when UAT and Production share a site code).
            _logger.LogError(
                "[OzowIntentReconcile] stage=orphan-notify reference={Reference} transactionId={TransactionId} " +
                "status={Status} amount={Amount} — hash verified but NO OrderIntent matches. If this status is " +
                "Complete the customer HAS BEEN CHARGED with no order. Check whether another environment shares this Ozow site code.",
                reference, payload.TransactionId, payload.Status, payload.Amount);
            return new OzowNotifyOutcome(false, "Unknown intent reference");
        }

        var mapped = MapOzowStatus(payload.Status);

        // Non-terminal (e.g. PendingInvestigation) — record and wait.
        if (!mapped.HasValue)
        {
            _logger.LogInformation(
                "[OzowIntentReconcile] stage=non-terminal reference={Reference} intentId={IntentId} status={Status} " +
                "— stored, no state change.",
                reference, intent.Id, payload.Status);
            return new OzowNotifyOutcome(true, $"Stored. No state change for status '{payload.Status}'.");
        }

        if (mapped.Value != PaymentStatus.Completed)
        {
            // Cancelled / abandoned / error. Release the intent so the
            // customer can retry with any provider. Never cancel an
            // already-converted intent — a late failure notification
            // must not undo a paid order.
            if (intent.Status != OrderIntentStatus.ConvertedToOrder
             && intent.Status != OrderIntentStatus.Cancelled)
            {
                intent.Status = OrderIntentStatus.Cancelled;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            _logger.LogInformation(
                "[OzowIntentReconcile] stage=not-paid reference={Reference} intentId={IntentId} status={Status} " +
                "intentStatus={IntentStatus} — no order created (correct).",
                reference, intent.Id, payload.Status, intent.Status);
            return new OzowNotifyOutcome(true, $"Intent {intent.Id} left unconverted for status '{payload.Status}'.");
        }

        // ─── Complete ─────────────────────────────────────────────────
        // Amount cross-check BEFORE converting. IntentPaymentAmount is
        // what we actually sent Ozow (post test-amount override), so this
        // compares like with like. A mismatch is a replay/tamper signal:
        // refuse to mint an order, and shout about it.
        var expectedAmount = intent.IntentPaymentAmount ?? intent.IntentInvoiceAmountAtTime ?? 0m;
        if (expectedAmount > 0m && Math.Abs(expectedAmount - payload.Amount) > 0.01m)
        {
            _logger.LogError(
                "[OzowIntentReconcile] stage=amount-mismatch reference={Reference} intentId={IntentId} " +
                "expected={Expected} got={Got} transactionId={TransactionId} — REFUSING to convert. " +
                "Money may have moved; reconcile manually against the Ozow dashboard.",
                reference, intent.Id, expectedAmount, payload.Amount, payload.TransactionId);
            return new OzowNotifyOutcome(false, "Amount mismatch");
        }

        var conversion = await _orderIntentService.ConvertIntentPaymentToPaidOrderAsync(
            intentPaymentReference: reference,
            paidAtUtc:              DateTime.UtcNow,
            gatewayTransactionId:   payload.TransactionId,
            authorizationSnapshot:  null,
            cancellationToken:      cancellationToken);

        if (!conversion.IsSuccess)
        {
            // The customer HAS paid. Conversion failing here is the
            // single worst state this flow can reach, so it is logged at
            // Error with everything needed to finish the job by hand.
            _logger.LogError(
                "[OzowIntentReconcile] stage=conversion-failed reference={Reference} intentId={IntentId} " +
                "transactionId={TransactionId} amount={Amount} code={Code} message={Message} — " +
                "PAYMENT TAKEN, ORDER NOT CREATED. Manual conversion required.",
                reference, intent.Id, payload.TransactionId, payload.Amount,
                conversion.Code, conversion.Message);
            return new OzowNotifyOutcome(false, conversion.Message ?? "Intent conversion failed");
        }

        _logger.LogInformation(
            "[OzowIntentReconcile] stage=converted reference={Reference} intentId={IntentId} " +
            "orderNumber={OrderNumber} invoiceNumber={InvoiceNumber} transactionId={TransactionId} amount={Amount}",
            reference, intent.Id,
            conversion.Data?.OrderNumber ?? "(none)",
            conversion.Data?.InvoiceNumber ?? "(none)",
            payload.TransactionId, payload.Amount);

        return new OzowNotifyOutcome(
            true,
            $"Intent {reference} converted to order {conversion.Data?.OrderNumber ?? "(unknown)"}.");
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

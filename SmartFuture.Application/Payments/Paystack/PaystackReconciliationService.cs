using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Payments.Mandates;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Payments.Paystack;

/// <summary>
/// Operator + customer-driven recovery path for Paystack payments.
///
/// Webhook delivery is best-effort: Paystack can be late, our endpoint
/// can be unreachable for a window, signatures can fail on a key
/// rotation. When a customer comes back from checkout with
/// <c>outcome=success</c> and the invoice is still unpaid, this
/// service is the authoritative way to close the loop:
///
///   1. Look up the SmartFuture <see cref="PaymentInitiation"/> by
///      ProviderReference.
///   2. Call <c>POST /transaction/verify/{reference}</c> with the
///      server-only secret key.
///   3. Cross-check amount / currency / transaction status.
///   4. If Paystack confirms paid AND nothing already settled it,
///      apply via <see cref="IPaymentApplierService"/> — the same
///      canonical path the webhook handler uses.
///   5. Best-effort store the reusable authorization on a mandate.
///
/// Idempotent: replay-safe because every mutation step is itself
/// idempotent (the applier short-circuits when status is unchanged;
/// the mandate upsert dedupes on AuthorizationSignature).
///
/// Authority is OUR DB + Paystack's server-side verify response.
/// The customer-facing endpoint is safe to expose anonymously because
/// the attacker has to guess a valid SF-PAY reference AND Paystack
/// has to confirm a matching paid transaction — neither is forgeable.
/// </summary>
public interface IPaystackReconciliationService
{
    Task<Result<PaystackReconciliationOutcomeDto>> ReconcileAsync(
        string reference, CancellationToken cancellationToken = default);
}

public class PaystackReconciliationService : IPaystackReconciliationService
{
    private readonly IAppDbContext _dbContext;
    private readonly PaystackVerificationService _verifier;
    private readonly IPaymentApplierService _applier;
    private readonly ICustomerPaymentMandateService _mandates;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly IHostEnvironment _env;
    private readonly ILogger<PaystackReconciliationService> _logger;

    public PaystackReconciliationService(
        IAppDbContext dbContext,
        PaystackVerificationService verifier,
        IPaymentApplierService applier,
        ICustomerPaymentMandateService mandates,
        IAuditService auditService,
        ICurrentUserService currentUser,
        IHostEnvironment env,
        ILogger<PaystackReconciliationService> logger)
    {
        _dbContext = dbContext;
        _verifier = verifier;
        _applier = applier;
        _mandates = mandates;
        _auditService = auditService;
        _currentUser = currentUser;
        _env = env;
        _logger = logger;
    }

    public async Task<Result<PaystackReconciliationOutcomeDto>> ReconcileAsync(
        string reference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return Result<PaystackReconciliationOutcomeDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "Reference is required.");
        }
        reference = reference.Trim();

        var outcome = new PaystackReconciliationOutcomeDto { Reference = reference };

        // 1) Find the PaymentInitiation we minted at initiate time.
        var initiation = await _dbContext.PaymentInitiations
            .Include(i => i.Invoice)
            .Include(i => i.Payment)
            .FirstOrDefaultAsync(i => i.Provider == PaymentProviderType.Paystack
                                   && i.ProviderReference == reference,
                                 cancellationToken);
        if (initiation is null || initiation.Payment is null || initiation.Invoice is null)
        {
            outcome.PaymentInitiationFound = false;
            outcome.ErrorCode    = ErrorCodes.NOT_FOUND;
            outcome.ErrorMessage = "No payment initiation found for that reference.";
            outcome.Errors.Add("No PaymentInitiation found for that reference.");
            _logger.LogWarning("[PaystackReconcile] unknown reference {Reference}", reference);
            return Result<PaystackReconciliationOutcomeDto>.Failure(
                ErrorCodes.NOT_FOUND, "No payment initiation found for that reference.");
        }
        outcome.PaymentInitiationFound = true;
        outcome.InvoiceId = initiation.InvoiceId;
        outcome.InvoiceNumber = initiation.Invoice.InvoiceNumber;
        outcome.PaymentId = initiation.Payment.Id;
        outcome.PaymentNumber = initiation.Payment.PaymentNumber;
        outcome.InvoiceStatusBefore = initiation.Invoice.Status.ToString();
        outcome.OverrideApplied = initiation.IsTestAmountOverrideApplied;
        outcome.InvoiceAmount = initiation.InvoiceAmountAtTime ?? initiation.Invoice.TotalAmount;
        outcome.ProviderAmount = initiation.ActualProviderAmount ?? initiation.Amount;

        // 2) Idempotency short-circuit.
        if (initiation.Payment.Status == PaymentStatus.Completed)
        {
            outcome.InvoiceStatusAfter = initiation.Invoice.Status.ToString();
            outcome.Actions.Add("payment-already-completed");
            outcome.PaystackStatus = "success";
            _logger.LogInformation(
                "[PaystackReconcile] payment {PaymentNumber} already Completed — no action.",
                initiation.Payment.PaymentNumber);
            return Result<PaystackReconciliationOutcomeDto>.Success(outcome, "Payment was already reconciled.");
        }

        // 3) Call Paystack /transaction/verify with secret key.
        var verifyResult = await _verifier.VerifyAsync(reference, cancellationToken);
        if (!verifyResult.IsSuccess)
        {
            outcome.ErrorCode    = ErrorCodes.UPSTREAM_UNAVAILABLE;
            outcome.ErrorMessage = verifyResult.Message ?? "Paystack verify failed.";
            outcome.Errors.Add($"Paystack verify call failed: {verifyResult.Message}");
            _logger.LogWarning(
                "[PaystackReconcile] verify failed for {Reference}: {Message}",
                reference, verifyResult.Message);
            return Result<PaystackReconciliationOutcomeDto>.Failure(
                ErrorCodes.UPSTREAM_UNAVAILABLE, verifyResult.Message ?? "Paystack verify failed.");
        }

        var verify = verifyResult.Data!;
        outcome.PaystackStatus = verify.Status;
        outcome.ProviderTransactionId = verify.ProviderTransactionId;

        // 4) Cross-checks. We ALREADY committed to the override-routed
        //    Payment.Amount at initiate (e.g. R10 for UAT), so a paid
        //    transaction MUST report the same amount.
        var expectedSubunits = ToSubunits(initiation.Payment.Amount);
        var expectedCurrency = (initiation.CurrencyCode ?? "ZAR").Trim().ToUpperInvariant();
        var actualCurrency = (verify.Currency ?? string.Empty).Trim().ToUpperInvariant();
        outcome.AmountExpectedSubunits = expectedSubunits;
        outcome.AmountReceivedSubunits = verify.AmountSubunits;
        outcome.CurrencyExpected = expectedCurrency;
        outcome.CurrencyReceived = actualCurrency;

        if (!string.Equals(verify.Status, "success", StringComparison.OrdinalIgnoreCase))
        {
            outcome.Actions.Add("paystack-status-not-success");
            outcome.Errors.Add($"Paystack reports status '{verify.Status}', not 'success'.");
            outcome.InvoiceStatusAfter = initiation.Invoice.Status.ToString();
            _logger.LogInformation(
                "[PaystackReconcile] reference={Reference} paystackStatus={Status} — not applying.",
                reference, verify.Status);
            return Result<PaystackReconciliationOutcomeDto>.Success(outcome,
                $"Paystack reports status '{verify.Status}'. Nothing was applied.");
        }
        if (verify.AmountSubunits != expectedSubunits)
        {
            outcome.Actions.Add("amount-mismatch");
            outcome.Errors.Add(
                $"Amount mismatch — expected {expectedSubunits} subunits, Paystack reports {verify.AmountSubunits}.");
            outcome.InvoiceStatusAfter = initiation.Invoice.Status.ToString();
            outcome.ErrorCode    = ErrorCodes.CONFLICT;
            outcome.ErrorMessage = "Amount mismatch between SmartFuture and Paystack.";
            _logger.LogWarning(
                "[PaystackReconcile] amount mismatch for {Reference}: expected {Expected} got {Got}",
                reference, expectedSubunits, verify.AmountSubunits);
            return Result<PaystackReconciliationOutcomeDto>.Failure(
                ErrorCodes.CONFLICT, "Amount mismatch between SmartFuture and Paystack.");
        }
        if (!string.IsNullOrWhiteSpace(actualCurrency)
            && !string.Equals(actualCurrency, expectedCurrency, StringComparison.Ordinal))
        {
            outcome.Actions.Add("currency-mismatch");
            outcome.Errors.Add($"Currency mismatch — expected {expectedCurrency}, Paystack reports {actualCurrency}.");
            outcome.InvoiceStatusAfter = initiation.Invoice.Status.ToString();
            outcome.ErrorCode    = ErrorCodes.CONFLICT;
            outcome.ErrorMessage = "Currency mismatch between SmartFuture and Paystack.";
            _logger.LogWarning(
                "[PaystackReconcile] currency mismatch for {Reference}: expected {Expected} got {Got}",
                reference, expectedCurrency, actualCurrency);
            return Result<PaystackReconciliationOutcomeDto>.Failure(
                ErrorCodes.CONFLICT, "Currency mismatch between SmartFuture and Paystack.");
        }

        // 5) Persist Paystack's transaction id + stamp the
        //    webhook-received column so admin tools show "received".
        if (!string.IsNullOrWhiteSpace(verify.ProviderTransactionId))
        {
            initiation.Payment.GatewayTransactionId = verify.ProviderTransactionId;
            initiation.ProviderCheckoutId = verify.ProviderTransactionId;
        }
        // We don't lie about the webhook field — reconciliation isn't a
        // webhook. Operators can grep [PaystackReconcile] for the
        // recovered-by-reconcile rows.
        await _dbContext.SaveChangesAsync(cancellationToken);

        // 6) Apply Completed via the canonical applier. Apply respects:
        //    - PaymentProcessing.WebhookApplyEnabled (kill-switch)
        //    - PaymentInitiation.WebhookApplyMode (per-row dry-run)
        //    - Payment.IsTestAmountOverrideApplied (UAT-only settlement)
        //    - env=Production (override settlement blocked)
        outcome.Actions.Add("apply-status-change-Completed");
        outcome.ApplyAttempted = true;
        var applyResult = await _applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = initiation.Payment.Id,
            NewStatus = PaymentStatus.Completed,
            GatewayTransactionId = verify.ProviderTransactionId,
            GatewayReference = reference,
            PaidAtUtc = verify.PaidAtUtc ?? DateTime.UtcNow,
            TriggerNotifications = true
        }, cancellationToken);

        if (!applyResult.IsSuccess)
        {
            outcome.ErrorCode    = applyResult.Code;
            outcome.ErrorMessage = applyResult.Message;
            outcome.Errors.Add($"Applier failed: {applyResult.Message}");
            _logger.LogError(
                "[PaystackReconcile] applier failed for {Reference}: {Code} {Message}",
                reference, applyResult.Code, applyResult.Message);
            return Result<PaystackReconciliationOutcomeDto>.Failure(
                applyResult.Code ?? ErrorCodes.EXCEPTION, applyResult.Message ?? "Apply failed.");
        }
        outcome.ApplySucceeded = true;

        // Re-read invoice to surface the post-apply status.
        var refreshed = await _dbContext.Invoices
            .AsNoTracking()
            .Where(i => i.Id == initiation.InvoiceId)
            .Select(i => new { i.Status, i.AmountPaid, i.BalanceDue })
            .FirstOrDefaultAsync(cancellationToken);
        outcome.InvoiceStatusAfter = refreshed?.Status.ToString();

        // 7) Audit. Reconciliation is operationally significant —
        //    payments shouldn't normally need it.
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = _currentUser.UserId.HasValue ? AuditActorType.Admin : AuditActorType.System,
            ActionType = AuditActionType.PaymentStatusChanged,
            EntityType = AuditEntityType.Payment,
            EntityId = initiation.Payment.Id,
            EntityName = initiation.Payment.PaymentNumber,
            Summary = $"Paystack payment reconciled via /verify for invoice {initiation.Invoice.InvoiceNumber}",
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                reference,
                paystackStatus = verify.Status,
                providerTransactionId = verify.ProviderTransactionId,
                invoiceId = initiation.InvoiceId,
                invoiceNumber = initiation.Invoice.InvoiceNumber,
                invoiceStatusBefore = outcome.InvoiceStatusBefore,
                invoiceStatusAfter = outcome.InvoiceStatusAfter,
                providerAmount = outcome.ProviderAmount,
                invoiceAmount = outcome.InvoiceAmount,
                overrideApplied = outcome.OverrideApplied,
                env = _env.EnvironmentName,
                source = "PaystackReconciliationService"
            }),
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        });

        _logger.LogWarning(
            "[PaystackReconcile] applied reference={Reference} invoice={InvoiceNumber} statusBefore={Before} statusAfter={After} providerAmount={ProviderAmount} invoiceAmount={InvoiceAmount} override={Override}",
            reference, initiation.Invoice.InvoiceNumber, outcome.InvoiceStatusBefore, outcome.InvoiceStatusAfter,
            outcome.ProviderAmount, outcome.InvoiceAmount, outcome.OverrideApplied);

        return Result<PaystackReconciliationOutcomeDto>.Success(outcome,
            $"Reconciled. Invoice {initiation.Invoice.InvoiceNumber} status: {outcome.InvoiceStatusBefore} → {outcome.InvoiceStatusAfter}.");
    }

    // ZAR → kobo / cents. Mirrors the conversion used by the
    // initiator + webhook handler.
    internal static long ToSubunits(decimal amount)
        => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
}

public class PaystackReconciliationOutcomeDto
{
    public string Reference { get; set; } = string.Empty;
    public string? PaystackStatus { get; set; }
    public string? ProviderTransactionId { get; set; }

    public Guid? InvoiceId { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? InvoiceStatusBefore { get; set; }
    public string? InvoiceStatusAfter { get; set; }

    public Guid? PaymentId { get; set; }
    public string? PaymentNumber { get; set; }

    public decimal ProviderAmount { get; set; }
    public decimal InvoiceAmount { get; set; }
    public bool OverrideApplied { get; set; }

    // Detailed diagnostics — added so the customer-facing
    // verify-and-apply call AND the admin reconcile both expose
    // enough surface to debug a stuck payment without log access.
    public bool PaymentInitiationFound { get; set; }
    public long? AmountExpectedSubunits { get; set; }
    public long? AmountReceivedSubunits { get; set; }
    public string? CurrencyExpected { get; set; }
    public string? CurrencyReceived { get; set; }
    public bool ApplyAttempted { get; set; }
    public bool ApplySucceeded { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Ordered list of internal step names taken.</summary>
    public List<string> Actions { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

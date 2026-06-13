using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Payments.Paystack;
using SmartFuture.Application.Payments.Recurring;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Payments;

public class AutoBillingService : IAutoBillingService
{
    private const string PaymentNumberPrefix = "PAY";
    private const int PaymentNumberMaxAttempts = 5;

    private readonly IAppDbContext _dbContext;
    // Provider-neutral charge dispatch (Phase 0A). Resolves the concrete
    // charge service by the mandate's provider. Only Paystack is
    // registered today; the previous hard dependency on
    // PaystackChargeAuthorizationService now lives behind this seam.
    private readonly IRecurringChargeServiceResolver _chargeResolver;
    private readonly IPaymentApplierService _applier;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly AutoBillingSettings _settings;
    private readonly Paystack.PaystackSettings _paystackSettings;
    private readonly IHostEnvironment _env;
    private readonly AutoBillingEmailService _email;
    private readonly ILogger<AutoBillingService> _logger;

    public AutoBillingService(
        IAppDbContext dbContext,
        IRecurringChargeServiceResolver chargeResolver,
        IPaymentApplierService applier,
        IAuditService auditService,
        ICurrentUserService currentUser,
        IOptions<AutoBillingSettings> settings,
        IOptions<Paystack.PaystackSettings> paystackSettings,
        IHostEnvironment env,
        AutoBillingEmailService email,
        ILogger<AutoBillingService> logger)
    {
        _dbContext = dbContext;
        _chargeResolver = chargeResolver;
        _applier = applier;
        _auditService = auditService;
        _currentUser = currentUser;
        _settings = settings.Value;
        _paystackSettings = paystackSettings.Value;
        _env = env;
        _email = email;
        _logger = logger;
    }

    public async Task<Result<AutoBillingChargeOutcome>> ChargeInvoiceAsync(
        Guid invoiceId, AutoBillingChargeSource source,
        Guid? executeRetryAttemptId = null, CancellationToken cancellationToken = default)
    {
        if (invoiceId == Guid.Empty)
            return Result<AutoBillingChargeOutcome>.Failure(ErrorCodes.BAD_REQUEST, "InvoiceId is required.");

        // Master gates first — cheap, no DB hit.
        if (!_settings.Enabled)
        {
            _logger.LogInformation("[AutoBilling] invoice {InvoiceId} skipped — AutoBilling.Enabled=false.", invoiceId);
            return Skipped("AutoBilling.Enabled=false");
        }
        if (!_settings.ChargeAuthorizationEnabled)
        {
            _logger.LogInformation("[AutoBilling] invoice {InvoiceId} skipped — AutoBilling.ChargeAuthorizationEnabled=false.", invoiceId);
            return Skipped("ChargeAuthorizationEnabled=false");
        }

        var invoice = await _dbContext.Invoices
            .Include(i => i.Order)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);
        if (invoice is null)
            return Result<AutoBillingChargeOutcome>.Failure(ErrorCodes.NOT_FOUND, "Invoice not found.");

        if (invoice.Status is InvoiceStatus.Paid or InvoiceStatus.Cancelled or InvoiceStatus.Void)
            return Skipped($"Invoice status is {invoice.Status}; nothing to charge.");

        if (invoice.BalanceDue <= 0m)
            return Skipped("Invoice balance is zero.");

        var userId = invoice.Order?.UserId;
        if (userId is null || userId == Guid.Empty)
            return Skipped("Invoice has no linked user.");

        // Customer opt-out check — authoritative. The retry job + monthly
        // job MUST also call through here so they honour the same flag.
        var profile = await _dbContext.CustomerProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId.Value, cancellationToken);
        if (profile is null || !profile.AutoBillingEnabled)
        {
            _logger.LogInformation(
                "[AutoBilling] invoice {InvoiceId} skipped — customer {UserId} has AutoBillingEnabled=false.",
                invoiceId, userId);
            return Skipped("Customer has not opted into auto-billing.");
        }

        // Resolve the default active reusable Paystack mandate.
        //
        // TODO (Phase 0C / PayFast recurring): make mandate selection
        // provider-neutral. This query is still HARD-SCOPED to
        // PaymentProviderType.Paystack. Phase 0A only introduces the
        // provider-neutral CHARGE seam (IRecurringChargeServiceResolver);
        // the SELECTION of which mandate/provider to bill must be widened
        // here — drop the `m.Provider == Paystack` filter and pick the
        // customer's default reusable mandate regardless of provider —
        // before any non-Paystack (e.g. PayFast) auto-billing can work.
        // Kept Paystack-scoped now so Phase 0A behaviour is byte-equivalent.
        var mandate = await _dbContext.CustomerPaymentMandates
            .Where(m => m.UserId == userId.Value
                     && m.Provider == PaymentProviderType.Paystack
                     && m.IsActive
                     && m.IsReusable
                     && m.IsDefault)
            .OrderByDescending(m => m.UpdatedAtUtc ?? m.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (mandate is null)
            return Skipped("No active default reusable Paystack mandate.");

        // ─── Phase 0D — retry reuse-mode validation ───────────────────
        //
        // When the retry worker passes an existing attempt id, validate it
        // and REUSE the row later instead of creating a new attempt. ALL
        // checks run BEFORE any Payment/PaymentInitiation is minted or any
        // provider call is made, so a bad/settled attempt is a clean no-op.
        // (Invoice-still-unpaid is already enforced by the Paid/Cancelled/
        // Void + BalanceDue<=0 guards above.)
        PaymentRetryAttempt? reuseAttempt = null;
        if (executeRetryAttemptId is Guid retryAttemptId)
        {
            reuseAttempt = await _dbContext.PaymentRetryAttempts
                .FirstOrDefaultAsync(a => a.Id == retryAttemptId, cancellationToken);
            if (reuseAttempt is null)
                return Skipped($"Retry attempt {retryAttemptId} not found.");
            if (reuseAttempt.InvoiceId != invoice.Id)
                return Skipped($"Retry attempt {retryAttemptId} does not belong to invoice {invoice.Id}.");
            if (reuseAttempt.Status != PaymentRetryAttemptStatus.Pending)
                return Skipped($"Retry attempt {retryAttemptId} is {reuseAttempt.Status}, not Pending.");
            if (reuseAttempt.AttemptNumber > _settings.MaxRetryAttempts)
                return Skipped($"Retry attempt {retryAttemptId} number {reuseAttempt.AttemptNumber} exceeds MaxRetryAttempts {_settings.MaxRetryAttempts}.");
        }

        var invoiceAmount = invoice.BalanceDue;
        var reference = PaystackChargeAuthorizationService.BuildAutoChargeReference();
        var now = DateTime.UtcNow;

        // ─── UAT live test-amount override for auto-debit ──────────────
        //
        // Same safety stack as PaystackPaymentInitiator: env must NOT
        // be Production, the AutoBilling flag must be on, TestAmount
        // must be > 0, and (when the Paystack key is live) the
        // Paystack.AllowLiveTestAmountOverride flag must also be on.
        // Hard-blocked in Production regardless of any flag.
        var paystackLiveKey = _paystackSettings.IsTestKey == false;
        var liveOverrideGate = !paystackLiveKey || _paystackSettings.AllowLiveTestAmountOverride;
        var overrideActive = _settings.UseTestAmountOverride
                          && _settings.TestAmount is > 0m
                          && !_env.IsProduction()
                          && liveOverrideGate;
        var chargeAmount = overrideActive ? _settings.TestAmount!.Value : invoiceAmount;
        if (overrideActive)
        {
            _logger.LogWarning(
                "[AutoBillingLiveUatOverride] invoiceAmount={InvoiceAmount} sentAmount={SentAmount} " +
                "invoice={InvoiceNumber} environment={Environment} source={Source} provider=Paystack " +
                "liveKey={LiveKey} allowLive={AllowLive}",
                invoiceAmount, chargeAmount, invoice.InvoiceNumber, _env.EnvironmentName,
                source, paystackLiveKey, _paystackSettings.AllowLiveTestAmountOverride);
        }
        else if (_settings.UseTestAmountOverride && _env.IsProduction())
        {
            _logger.LogError(
                "[AutoBillingLiveUatOverride] BLOCKED — AutoBilling__UseTestAmountOverride is true but environment is Production. " +
                "Using real invoice amount {Amount} for invoice {InvoiceNumber}.",
                invoiceAmount, invoice.InvoiceNumber);
        }
        else if (_settings.UseTestAmountOverride && paystackLiveKey && !_paystackSettings.AllowLiveTestAmountOverride)
        {
            _logger.LogError(
                "[AutoBillingLiveUatOverride] BLOCKED — AutoBilling__UseTestAmountOverride is true on a LIVE Paystack key but " +
                "Paystack__AllowLiveTestAmountOverride is false. Using real invoice amount {Amount} for invoice {InvoiceNumber}.",
                invoiceAmount, invoice.InvoiceNumber);
        }

        // Mint Payment + PaymentInitiation BEFORE the charge so the
        // record exists even if the HTTP call crashes mid-flight.
        // Status flips after the charge response lands.
        var payment = new Payment
        {
            InvoiceId = invoice.Id,
            Status = PaymentStatus.Pending,
            Method = PaymentMethodType.Gateway,
            Amount = chargeAmount,
            CurrencyCode = invoice.CurrencyCode,
            GatewayName = PaymentProviderType.Paystack.ToString(),
            GatewayReference = reference,
            LastStatusChangedByUserId = _currentUser.UserId,
            IsTestAmountOverrideApplied = overrideActive,
            ActualProviderAmount = overrideActive ? chargeAmount : (decimal?)null,
            InvoiceAmountAtTime = overrideActive ? invoiceAmount : (decimal?)null,
            TestOverrideReason = overrideActive
                ? $"AutoBilling UAT R{chargeAmount:0.00} override (source={source}, env={_env.EnvironmentName})"
                : null
        };

        var paymentNumber = await GenerateUniquePaymentNumberAsync(now, cancellationToken);
        if (paymentNumber is null)
            return Result<AutoBillingChargeOutcome>.Failure(
                ErrorCodes.EXCEPTION, "Could not generate a unique payment number.");
        payment.PaymentNumber = paymentNumber;
        _dbContext.Payments.Add(payment);

        var initiation = new PaymentInitiation
        {
            InvoiceId = invoice.Id,
            PaymentId = payment.Id,
            Provider = PaymentProviderType.Paystack,
            Status = PaymentInitiationStatus.Pending,
            Amount = chargeAmount,
            CurrencyCode = invoice.CurrencyCode,
            ProviderReference = reference,
            WebhookApplyMode = WebhookApplyMode.ApplyNormally,
            IsTestAmountOverrideApplied = overrideActive,
            ActualProviderAmount = overrideActive ? chargeAmount : (decimal?)null,
            InvoiceAmountAtTime = overrideActive ? invoiceAmount : (decimal?)null,
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                source = source.ToString(),
                mandateId = mandate.Id,
                reference,
                channel = mandate.Channel,
                last4 = mandate.Last4,
                overrideActive,
                invoiceAmount,
                chargeAmount
            })
        };
        _dbContext.PaymentInitiations.Add(initiation);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // ─── Resolve the PaymentRetryAttempt row for this execution ──────
        //
        // Reuse mode (Phase 0D): the retry worker passed an existing Pending
        // attempt — reuse it (no new row, keep its AttemptNumber) so the
        // chain doesn't duplicate or runaway.
        //
        // Fresh mode (install hook / monthly Stage 2 / admin run-test):
        // attempt 1 = initial; each prior attempt for this invoice bumps the
        // number. Unchanged from before.
        PaymentRetryAttempt attempt;
        int attemptNumber;
        if (reuseAttempt is not null)
        {
            attempt = reuseAttempt;
            attemptNumber = attempt.AttemptNumber;
            attempt.MandateId = mandate.Id;
            attempt.Provider = PaymentProviderType.Paystack;
            attempt.Amount = invoiceAmount;
            attempt.ProviderAmount = chargeAmount;
            attempt.ProviderReference = reference;
            attempt.Source = source;
            attempt.AttemptedUtc = now;
            attempt.PaymentId = payment.Id;
            attempt.UpdatedAtUtc = now;
            // Status stays Pending until the charge result lands; row is
            // already tracked, so no Add().
        }
        else
        {
            var priorAttempts = await _dbContext.PaymentRetryAttempts
                .CountAsync(a => a.InvoiceId == invoice.Id, cancellationToken);
            attemptNumber = priorAttempts + 1;

            attempt = new PaymentRetryAttempt
            {
                InvoiceId = invoice.Id,
                CustomerId = userId.Value,
                MandateId = mandate.Id,
                Provider = PaymentProviderType.Paystack,
                AttemptNumber = attemptNumber,
                Amount = invoiceAmount,
                ProviderAmount = chargeAmount,
                Status = PaymentRetryAttemptStatus.Pending,
                ProviderReference = reference,
                Source = source,
                ScheduledForUtc = now,
                AttemptedUtc = now,
                PaymentId = payment.Id
            };
            _dbContext.PaymentRetryAttempts.Add(attempt);
        }
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "[AutoBilling] charging invoice {InvoiceId} customer {UserId} mandate {MandateId} invoiceAmount={InvoiceAmount} chargeAmount={ChargeAmount} reference={Reference} source={Source} attempt={Attempt}/{Max} override={Override}",
            invoice.Id, userId, mandate.Id, invoiceAmount, chargeAmount, reference, source,
            attemptNumber, _settings.MaxRetryAttempts, overrideActive);

        // Provider-neutral dispatch. Resolves by mandate.Provider; today
        // that is always Paystack (selection is still Paystack-scoped — see
        // the TODO at the mandate query above). The mapping inside
        // PaystackRecurringChargeService is byte-equivalent to the inline
        // Paystack handling this block used to contain.
        var chargeContext = new RecurringChargeContext(
            InvoiceId: invoice.Id,
            CustomerId: userId.Value,
            Source: source,
            IsDryRun: false);
        var chargeResult = await _chargeResolver
            .Resolve(mandate.Provider)
            .ChargeAsync(mandate, chargeAmount, reference, chargeContext, cancellationToken);

        // Asynchronous settlement (e.g. future PayFast ad-hoc via ITN).
        // NOT reachable in Phase 0A — Paystack settles synchronously and no
        // Pending-returning provider is registered. Defensive: leave
        // Payment / PaymentInitiation / PaymentRetryAttempt in their Pending
        // state and DO NOT mark the invoice paid — settlement must arrive
        // via the provider webhook, never on the charge response alone.
        if (chargeResult.Kind == RecurringChargeOutcomeKind.Pending)
        {
            _logger.LogWarning(
                "[AutoBilling] invoice {InvoiceId} reference {Reference} returned PENDING from provider {Provider}; leaving records pending for webhook settlement.",
                invoice.Id, reference, mandate.Provider);
            return Result<AutoBillingChargeOutcome>.Success(new AutoBillingChargeOutcome(
                Charged: false,
                Reference: reference,
                PaymentId: payment.Id,
                PaymentInitiationId: initiation.Id,
                FailureReason: "Awaiting asynchronous provider settlement."));
        }

        if (chargeResult.Kind == RecurringChargeOutcomeKind.Failed)
        {
            // Capture the provider transaction id when present (the
            // provider-rejected path supplies one; the precondition-failure
            // path does not, so this is a no-op there). The
            // "Charge precondition failed." fallback preserves the prior
            // message when the provider returned no message at all.
            if (!string.IsNullOrWhiteSpace(chargeResult.ProviderTransactionId))
                payment.GatewayTransactionId = chargeResult.ProviderTransactionId;
            await PersistFailureAsync(invoice, payment, initiation, attempt, mandate,
                source, attemptNumber, invoiceAmount,
                chargeResult.Message ?? "Charge precondition failed.",
                cancellationToken);
            return Result<AutoBillingChargeOutcome>.Success(new AutoBillingChargeOutcome(
                Charged: false,
                Reference: reference,
                PaymentId: payment.Id,
                PaymentInitiationId: initiation.Id,
                FailureReason: chargeResult.Message));
        }

        // Success. Persist provider transaction id THEN apply Completed
        // through the canonical applier so invoice balance + audit +
        // notifications stay consistent with the webhook-driven path.
        if (!string.IsNullOrWhiteSpace(chargeResult.ProviderTransactionId))
        {
            payment.GatewayTransactionId = chargeResult.ProviderTransactionId;
            initiation.ProviderCheckoutId = chargeResult.ProviderTransactionId;
        }
        await _dbContext.SaveChangesAsync(cancellationToken);

        var applyResult = await _applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = payment.Id,
            NewStatus = PaymentStatus.Completed,
            GatewayTransactionId = chargeResult.ProviderTransactionId,
            GatewayReference = reference,
            PaidAtUtc = chargeResult.PaidAtUtc ?? now,
            TriggerNotifications = true
        }, cancellationToken);

        if (!applyResult.IsSuccess)
        {
            _logger.LogError(
                "[AutoBilling] applier failed after successful charge for invoice {InvoiceId} reference {Reference}: {Code} {Message}",
                invoice.Id, reference, applyResult.Code, applyResult.Message);
            await PersistFailureAsync(invoice, payment, initiation, attempt, mandate,
                source, attemptNumber, invoiceAmount,
                $"Applier failed after successful charge: {applyResult.Message}",
                cancellationToken);
            return Result<AutoBillingChargeOutcome>.Success(new AutoBillingChargeOutcome(
                Charged: false,
                Reference: reference,
                PaymentId: payment.Id,
                PaymentInitiationId: initiation.Id,
                FailureReason: applyResult.Message));
        }

        // Success — mark the retry attempt and any other Pending attempts
        // for this invoice as resolved.
        attempt.Status = PaymentRetryAttemptStatus.Success;
        attempt.UpdatedAtUtc = DateTime.UtcNow;
        await CancelOtherPendingAttemptsAsync(invoice.Id, attempt.Id, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await EmitAuditAsync(invoice, payment, mandate, source, success: true, null);

        // Best-effort success email. Failure email infra is logged but
        // never throws — billing must not depend on SMTP.
        var recipientEmail = await ResolveCustomerEmailAsync(userId.Value, cancellationToken);
        if (!string.IsNullOrWhiteSpace(recipientEmail))
        {
            await _email.SendSuccessEmailAsync(
                invoice, recipientEmail!, userId.Value,
                amount: invoiceAmount,
                nextBillingDateUtc: now.AddDays(30),
                cancellationToken);
        }

        return Result<AutoBillingChargeOutcome>.Success(new AutoBillingChargeOutcome(
            Charged: true,
            Reference: reference,
            PaymentId: payment.Id,
            PaymentInitiationId: initiation.Id,
            FailureReason: null));
    }

    /// <summary>
    /// Single chokepoint for the three failure exits (precondition
    /// failed, Paystack-rejected, applier-rejected). Marks Payment +
    /// PaymentInitiation + PaymentRetryAttempt Failed, schedules the
    /// next retry if we have budget, and fires the failure email.
    /// </summary>
    private async Task PersistFailureAsync(
        Invoice invoice, Payment payment, PaymentInitiation initiation, PaymentRetryAttempt attempt,
        CustomerPaymentMandate mandate, AutoBillingChargeSource source,
        int attemptNumber, decimal invoiceAmount, string failureReason,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        payment.Status = PaymentStatus.Failed;
        payment.FailedAtUtc = now;
        payment.FailureReason = failureReason;
        initiation.Status = PaymentInitiationStatus.Failed;
        initiation.FailureReason = failureReason;
        attempt.Status = PaymentRetryAttemptStatus.Failed;
        attempt.FailureReason = failureReason;
        attempt.UpdatedAtUtc = now;

        // Schedule next retry if we have budget left AND retry job
        // would honour it (the worker is Phase 5, but rows are useful
        // for /run-test verification today).
        DateTime? nextRetry = null;
        if (attemptNumber < _settings.MaxRetryAttempts)
        {
            nextRetry = now.AddDays(Math.Max(1, _settings.RetryIntervalDays));
            _dbContext.PaymentRetryAttempts.Add(new PaymentRetryAttempt
            {
                InvoiceId = invoice.Id,
                CustomerId = attempt.CustomerId,
                MandateId = mandate.Id,
                Provider = PaymentProviderType.Paystack,
                AttemptNumber = attemptNumber + 1,
                Amount = invoiceAmount,
                ProviderAmount = null,
                Status = PaymentRetryAttemptStatus.Pending,
                Source = AutoBillingChargeSource.Retry,
                ScheduledForUtc = nextRetry.Value
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await EmitAuditAsync(invoice, payment, mandate, source, success: false, failureReason);

        // Best-effort email — never throws.
        var recipientEmail = await ResolveCustomerEmailAsync(attempt.CustomerId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(recipientEmail))
        {
            await _email.SendFailureEmailAsync(
                invoice, recipientEmail!, attempt.CustomerId,
                amount: invoiceAmount,
                failureReason: failureReason,
                attemptNumber: attemptNumber,
                maxAttempts: _settings.MaxRetryAttempts,
                nextRetryUtc: nextRetry,
                cancellationToken);
        }
    }

    /// <summary>
    /// When an attempt succeeds (or when an admin manually pays the
    /// invoice), any other Pending retry rows for the same invoice
    /// should be flipped to Skipped so the worker doesn't re-charge
    /// after the fact.
    /// </summary>
    private async Task CancelOtherPendingAttemptsAsync(Guid invoiceId, Guid keepAttemptId, CancellationToken cancellationToken)
    {
        var pending = await _dbContext.PaymentRetryAttempts
            .Where(a => a.InvoiceId == invoiceId
                     && a.Id != keepAttemptId
                     && a.Status == PaymentRetryAttemptStatus.Pending)
            .ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;
        foreach (var a in pending)
        {
            a.Status = PaymentRetryAttemptStatus.Skipped;
            a.UpdatedAtUtc = now;
            a.FailureReason = "Cancelled — invoice settled by another attempt or manual payment.";
        }
    }

    private async Task<string?> ResolveCustomerEmailAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await _dbContext.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Result<AutoBillingCycleSummaryDto>> RunAutoBillingCycleAsync(
        string? userEmail = null, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        var runId = Guid.NewGuid();
        var startedAt = DateTime.UtcNow;
        var summary = new AutoBillingCycleSummaryDto
        {
            RunId = runId,
            StartedAtUtc = startedAt,
            DryRun = dryRun,
            UserEmailFilter = userEmail,
            TestAmountOverrideApplied = _settings.UseTestAmountOverride
                                     && _settings.TestAmount is > 0m
                                     && !_env.IsProduction(),
            OverrideTestAmount = _settings.TestAmount
        };

        // Hard production gate. Defence-in-depth: the admin controller
        // also blocks the endpoint in Production, but the SERVICE
        // refuses too so a future scheduler / SDK caller can't bypass.
        if (_env.IsProduction())
        {
            summary.FinishedAtUtc = DateTime.UtcNow;
            summary.Errors.Add("RunAutoBillingCycleAsync is hard-blocked in Production.");
            _logger.LogError(
                "[AutoBillingCycle:Blocked] env=Production, refusing run (dryRun={DryRun}, userEmail={UserEmail})",
                dryRun, userEmail ?? "(all)");
            return Result<AutoBillingCycleSummaryDto>.Failure(ErrorCodes.FORBIDDEN,
                "Auto-billing cycle is not available in Production.");
        }

        if (!_settings.Enabled)
        {
            summary.FinishedAtUtc = DateTime.UtcNow;
            summary.Errors.Add("AutoBilling.Enabled=false");
            return Result<AutoBillingCycleSummaryDto>.Success(summary,
                "Auto-billing is disabled; nothing to do.");
        }

        _logger.LogWarning(
            "[AutoBillingCycle:Started] runId={RunId} dryRun={DryRun} userEmail={UserEmail} override={Override} env={Env}",
            runId, dryRun, userEmail ?? "(all)", summary.TestAmountOverrideApplied, _env.EnvironmentName);

        // ─── Find candidate invoices ──────────────────────────────────
        //
        // Scope: Issued, Overdue, PartiallyPaid invoices that belong to
        // a customer who has opted in to auto-billing and has an
        // active default reusable Paystack mandate. If a userEmail is
        // supplied we constrain to that user only.
        //
        // We intentionally do NOT pre-filter on "due for renewal" date
        // logic — the Service entity that would carry NextBillingDateUtc
        // doesn't exist yet (deferred). For UAT the operator's intent
        // is "charge every outstanding invoice you can"; the monthly
        // job (Phase 6) will add the date gate when shipped.
        var candidatesQuery = _dbContext.Invoices
            .Include(i => i.Order)
            .Where(i => i.Status == InvoiceStatus.Issued
                     || i.Status == InvoiceStatus.Overdue
                     || i.Status == InvoiceStatus.PartiallyPaid)
            .Where(i => i.Order != null);

        if (!string.IsNullOrWhiteSpace(userEmail))
        {
            var emailTrim = userEmail.Trim();
            candidatesQuery = candidatesQuery.Where(i =>
                i.Order!.User != null && i.Order.User.Email == emailTrim);
        }

        var candidates = await candidatesQuery
            .OrderBy(i => i.IssuedAtUtc ?? i.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        summary.ServicesScanned = candidates.Count;

        foreach (var invoice in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = new AutoBillingCycleItemDto
            {
                InvoiceId = invoice.Id,
                InvoiceNumber = invoice.InvoiceNumber,
                InvoiceBalanceDue = invoice.BalanceDue,
                Source = AutoBillingChargeSource.AdminManual
            };

            var customerId = invoice.Order?.UserId;
            if (customerId is null || customerId == Guid.Empty)
            {
                item.Outcome = "Skipped";
                item.Reason = "Invoice has no linked customer.";
                summary.Items.Add(item);
                continue;
            }
            item.CustomerId = customerId.Value;
            item.CustomerEmail = await ResolveCustomerEmailAsync(customerId.Value, cancellationToken);

            if (invoice.BalanceDue <= 0m || invoice.Status == InvoiceStatus.Paid)
            {
                item.Outcome = "AlreadyPaid";
                item.Reason = $"Invoice is in status {invoice.Status} with balance R{invoice.BalanceDue:0.00}.";
                summary.UpToDateCount++;
                summary.Items.Add(item);
                continue;
            }

            var profile = await _dbContext.CustomerProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == customerId.Value, cancellationToken);
            if (profile is null || !profile.AutoBillingEnabled)
            {
                item.Outcome = "NotOptedIn";
                item.Reason = "Customer has not opted in to auto-billing.";
                summary.ManualPaymentRequiredCount++;
                summary.Items.Add(item);
                continue;
            }

            var mandate = await _dbContext.CustomerPaymentMandates
                .AsNoTracking()
                .Where(m => m.UserId == customerId.Value
                         && m.Provider == PaymentProviderType.Paystack
                         && m.IsActive && m.IsReusable && m.IsDefault)
                .OrderByDescending(m => m.UpdatedAtUtc ?? m.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (mandate is null)
            {
                item.Outcome = "NoMandate";
                item.Reason = "No active default reusable Paystack mandate.";
                summary.ManualPaymentRequiredCount++;
                summary.Items.Add(item);
                continue;
            }

            summary.ServicesDue++;

            if (dryRun)
            {
                item.Outcome = "DryRunOnly";
                item.Reason = $"Would charge R{invoice.BalanceDue:0.00} via Paystack mandate ({mandate.CardType} •••• {mandate.Last4}).";
                item.AmountChargedToProvider = summary.TestAmountOverrideApplied
                    ? _settings.TestAmount
                    : invoice.BalanceDue;
                summary.Items.Add(item);
                continue;
            }

            summary.ChargesAttempted++;
            var chargeResult = await ChargeInvoiceAsync(invoice.Id, AutoBillingChargeSource.AdminManual, cancellationToken: cancellationToken);
            if (!chargeResult.IsSuccess)
            {
                item.Outcome = "Failed";
                item.Reason = chargeResult.Message;
                summary.ChargesFailed++;
                summary.Errors.Add($"Invoice {invoice.InvoiceNumber}: {chargeResult.Message}");
                summary.Items.Add(item);
                continue;
            }

            var co = chargeResult.Data!;
            item.PaymentId = co.PaymentId;
            item.PaymentInitiationId = co.PaymentInitiationId;
            item.ProviderReference = co.Reference;

            if (co.Charged)
            {
                item.Outcome = "Charged";
                item.AmountChargedToProvider = summary.TestAmountOverrideApplied
                    ? _settings.TestAmount
                    : invoice.BalanceDue;
                summary.ChargesSucceeded++;
            }
            else
            {
                item.Outcome = "Failed";
                item.Reason = co.FailureReason;
                summary.ChargesFailed++;

                // Look up the latest retry attempt row we just wrote so
                // the operator sees attempt-number + next-retry in the
                // summary.
                var latest = await _dbContext.PaymentRetryAttempts
                    .AsNoTracking()
                    .Where(a => a.InvoiceId == invoice.Id)
                    .OrderByDescending(a => a.CreatedAtUtc)
                    .FirstOrDefaultAsync(cancellationToken);
                if (latest is not null)
                {
                    item.AttemptNumber = latest.AttemptNumber;
                    item.RetryAttemptId = latest.Id;
                }
                var nextRetry = await _dbContext.PaymentRetryAttempts
                    .AsNoTracking()
                    .Where(a => a.InvoiceId == invoice.Id
                             && a.Status == PaymentRetryAttemptStatus.Pending
                             && a.ScheduledForUtc > startedAt)
                    .OrderBy(a => a.ScheduledForUtc)
                    .FirstOrDefaultAsync(cancellationToken);
                if (nextRetry is not null)
                {
                    item.NextRetryScheduledUtc = nextRetry.ScheduledForUtc;
                    summary.RetriesScheduled++;
                }
                else
                {
                    summary.RetriesSkipped++;
                }
            }

            summary.Items.Add(item);
        }

        summary.FinishedAtUtc = DateTime.UtcNow;
        _logger.LogWarning(
            "[AutoBillingCycle:Finished] runId={RunId} attempted={Attempted} succeeded={Succeeded} failed={Failed} retriesScheduled={Retries} upToDate={UpToDate} manualRequired={Manual} dryRun={DryRun}",
            runId, summary.ChargesAttempted, summary.ChargesSucceeded, summary.ChargesFailed,
            summary.RetriesScheduled, summary.UpToDateCount, summary.ManualPaymentRequiredCount, dryRun);

        return Result<AutoBillingCycleSummaryDto>.Success(summary,
            $"Auto-billing cycle finished. Attempted {summary.ChargesAttempted}, succeeded {summary.ChargesSucceeded}, failed {summary.ChargesFailed}.");
    }

    private static Result<AutoBillingChargeOutcome> Skipped(string reason) =>
        Result<AutoBillingChargeOutcome>.Success(new AutoBillingChargeOutcome(
            Charged: false, Reference: null, PaymentId: null, PaymentInitiationId: null, FailureReason: reason));

    private async Task<string?> GenerateUniquePaymentNumberAsync(DateTime now, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < PaymentNumberMaxAttempts; attempt++)
        {
            var candidate = BillingNumberGenerator.BuildCandidate(PaymentNumberPrefix, now);
            var exists = await _dbContext.Payments.AnyAsync(p => p.PaymentNumber == candidate, cancellationToken);
            if (!exists) return candidate;
        }
        return null;
    }

    private async Task EmitAuditAsync(
        Invoice invoice, Payment payment, Domain.Billing.CustomerPaymentMandate mandate,
        AutoBillingChargeSource source, bool success, string? failureReason)
    {
        var actionType = success
            ? AuditActionType.AutoBillingChargeSucceeded
            : AuditActionType.AutoBillingChargeFailed;

        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = AuditActorType.System,
            ActionType = actionType,
            EntityType = AuditEntityType.Payment,
            EntityId = payment.Id,
            EntityName = payment.PaymentNumber,
            Summary = success
                ? $"Auto-debit succeeded for invoice {invoice.InvoiceNumber} ({mandate.CardType} •••• {mandate.Last4})"
                : $"Auto-debit failed for invoice {invoice.InvoiceNumber}: {failureReason}",
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                invoiceId = invoice.Id,
                invoiceNumber = invoice.InvoiceNumber,
                paymentNumber = payment.PaymentNumber,
                mandateId = mandate.Id,
                provider = "Paystack",
                amount = payment.Amount,
                currency = payment.CurrencyCode,
                source = source.ToString(),
                last4 = mandate.Last4,
                channel = mandate.Channel
            }),
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = success,
            FailureReason = failureReason
        });
    }
}

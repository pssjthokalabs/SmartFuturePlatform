using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Payments.Paystack;
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
    private readonly PaystackChargeAuthorizationService _chargeService;
    private readonly IPaymentApplierService _applier;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly AutoBillingSettings _settings;
    private readonly ILogger<AutoBillingService> _logger;

    public AutoBillingService(
        IAppDbContext dbContext,
        PaystackChargeAuthorizationService chargeService,
        IPaymentApplierService applier,
        IAuditService auditService,
        ICurrentUserService currentUser,
        IOptions<AutoBillingSettings> settings,
        ILogger<AutoBillingService> logger)
    {
        _dbContext = dbContext;
        _chargeService = chargeService;
        _applier = applier;
        _auditService = auditService;
        _currentUser = currentUser;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<Result<AutoBillingChargeOutcome>> ChargeInvoiceAsync(
        Guid invoiceId, AutoBillingChargeSource source, CancellationToken cancellationToken = default)
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

        var amount = invoice.BalanceDue;
        var reference = PaystackChargeAuthorizationService.BuildAutoChargeReference();
        var now = DateTime.UtcNow;

        // Mint Payment + PaymentInitiation BEFORE the charge so the
        // record exists even if the HTTP call crashes mid-flight.
        // Status flips after the charge response lands.
        var payment = new Payment
        {
            InvoiceId = invoice.Id,
            Status = PaymentStatus.Pending,
            Method = PaymentMethodType.Gateway,
            Amount = amount,
            CurrencyCode = invoice.CurrencyCode,
            GatewayName = PaymentProviderType.Paystack.ToString(),
            GatewayReference = reference,
            LastStatusChangedByUserId = _currentUser.UserId
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
            Amount = amount,
            CurrencyCode = invoice.CurrencyCode,
            ProviderReference = reference,
            WebhookApplyMode = WebhookApplyMode.ApplyNormally,
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                source = source.ToString(),
                mandateId = mandate.Id,
                reference,
                channel = mandate.Channel,
                last4 = mandate.Last4
            })
        };
        _dbContext.PaymentInitiations.Add(initiation);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "[AutoBilling] charging invoice {InvoiceId} customer {UserId} mandate {MandateId} amount={Amount} reference={Reference} source={Source}",
            invoice.Id, userId, mandate.Id, amount, reference, source);

        var chargeResult = await _chargeService.ChargeAsync(mandate.Id, amount, reference, cancellationToken);

        // chargeService failure result == precondition failed (e.g. gates flipped mid-flight).
        if (!chargeResult.IsSuccess)
        {
            payment.Status = PaymentStatus.Failed;
            payment.FailedAtUtc = now;
            payment.FailureReason = chargeResult.Message;
            initiation.Status = PaymentInitiationStatus.Failed;
            initiation.FailureReason = chargeResult.Message;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await EmitAuditAsync(invoice, payment, mandate, source, success: false, chargeResult.Message);
            return Result<AutoBillingChargeOutcome>.Success(new AutoBillingChargeOutcome(
                Charged: false,
                Reference: reference,
                PaymentId: payment.Id,
                PaymentInitiationId: initiation.Id,
                FailureReason: chargeResult.Message));
        }

        var outcome = chargeResult.Data!;
        if (!outcome.Success)
        {
            payment.Status = PaymentStatus.Failed;
            payment.FailedAtUtc = now;
            payment.FailureReason = outcome.GatewayMessage ?? outcome.Status;
            initiation.Status = PaymentInitiationStatus.Failed;
            initiation.FailureReason = outcome.GatewayMessage ?? outcome.Status;
            if (!string.IsNullOrWhiteSpace(outcome.ProviderTransactionId))
                payment.GatewayTransactionId = outcome.ProviderTransactionId;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await EmitAuditAsync(invoice, payment, mandate, source, success: false, outcome.GatewayMessage);
            return Result<AutoBillingChargeOutcome>.Success(new AutoBillingChargeOutcome(
                Charged: false,
                Reference: reference,
                PaymentId: payment.Id,
                PaymentInitiationId: initiation.Id,
                FailureReason: outcome.GatewayMessage ?? $"Paystack returned status '{outcome.Status}'."));
        }

        // Success. Persist provider transaction id THEN apply Completed
        // through the canonical applier so invoice balance + audit +
        // notifications stay consistent with the webhook-driven path.
        if (!string.IsNullOrWhiteSpace(outcome.ProviderTransactionId))
        {
            payment.GatewayTransactionId = outcome.ProviderTransactionId;
            initiation.ProviderCheckoutId = outcome.ProviderTransactionId;
        }
        await _dbContext.SaveChangesAsync(cancellationToken);

        var applyResult = await _applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = payment.Id,
            NewStatus = PaymentStatus.Completed,
            GatewayTransactionId = outcome.ProviderTransactionId,
            GatewayReference = reference,
            PaidAtUtc = outcome.PaidAtUtc ?? now,
            TriggerNotifications = true
        }, cancellationToken);

        if (!applyResult.IsSuccess)
        {
            _logger.LogError(
                "[AutoBilling] applier failed after successful charge for invoice {InvoiceId} reference {Reference}: {Code} {Message}",
                invoice.Id, reference, applyResult.Code, applyResult.Message);
            await EmitAuditAsync(invoice, payment, mandate, source, success: false,
                $"Applier failed after successful charge: {applyResult.Message}");
            return Result<AutoBillingChargeOutcome>.Success(new AutoBillingChargeOutcome(
                Charged: false,
                Reference: reference,
                PaymentId: payment.Id,
                PaymentInitiationId: initiation.Id,
                FailureReason: applyResult.Message));
        }

        await EmitAuditAsync(invoice, payment, mandate, source, success: true, null);

        return Result<AutoBillingChargeOutcome>.Success(new AutoBillingChargeOutcome(
            Charged: true,
            Reference: reference,
            PaymentId: payment.Id,
            PaymentInitiationId: initiation.Id,
            FailureReason: null));
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

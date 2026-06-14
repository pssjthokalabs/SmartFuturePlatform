using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments.Mandates;
using SmartFuture.Application.Payments.PayFast;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Phase 1B — PayFast implementation of <see cref="IRecurringChargeService"/>.
/// Decrypts the stored PayFast token and charges it via the ad-hoc API.
///
/// SETTLEMENT: PayFast ad-hoc charges settle ASYNCHRONOUSLY via ITN, so an
/// accepted API response maps to <see cref="RecurringChargeOutcomeKind.Pending"/>
/// — the invoice is NEVER marked paid from the API response, and this service
/// NEVER calls PaymentApplierService. Settlement happens when the PayFast ITN
/// (carrying the same <c>m_payment_id</c>) reaches the existing notify handler.
///
/// INERT until Phase 1C: registered as an IRecurringChargeService, but the
/// engine's mandate selection is still Paystack-scoped, so nothing resolves
/// this service yet. Also gated by <c>PayFast__AdhocChargingEnabled</c>.
/// The token is NEVER logged.
/// </summary>
public sealed class PayFastRecurringChargeService : IRecurringChargeService
{
    private const string DefaultItemName = "SmartFuture recurring service";

    private readonly IPayFastAdhocChargeService _adhoc;
    private readonly IMandateProtector _protector;
    private readonly PayFastSettings _settings;
    private readonly ILogger<PayFastRecurringChargeService> _logger;

    public PayFastRecurringChargeService(
        IPayFastAdhocChargeService adhoc,
        IMandateProtector protector,
        IOptions<PayFastSettings> settings,
        ILogger<PayFastRecurringChargeService> logger)
    {
        _adhoc = adhoc;
        _protector = protector;
        _settings = settings.Value;
        _logger = logger;
    }

    public PaymentProviderType Provider => PaymentProviderType.PayFast;

    public async Task<RecurringChargeResult> ChargeAsync(
        CustomerPaymentMandate mandate,
        decimal amountZar,
        string reference,
        RecurringChargeContext context,
        CancellationToken cancellationToken = default)
    {
        // Master gate — never call the PayFast API when disabled.
        if (!_settings.AdhocChargingEnabled)
        {
            _logger.LogInformation(
                "[payment][payfast][adhoc] skipped — PayFast__AdhocChargingEnabled=false (invoice {InvoiceId} reference {Reference}).",
                context.InvoiceId, reference);
            return RecurringChargeResult.Failed("PayFast ad-hoc charging is disabled.");
        }

        // Defensive dry-run guard. Phase 0 runners gate before charge, so
        // this should be unreachable — if it fires, something upstream is
        // wrong; never make an external call.
        if (context.IsDryRun)
        {
            _logger.LogWarning(
                "[payment][payfast][adhoc][dry_run_blocked] charge suppressed for invoice {InvoiceId} reference {Reference} — dry-run reached the provider service unexpectedly.",
                context.InvoiceId, reference);
            return RecurringChargeResult.Failed("Dry-run: PayFast ad-hoc charge suppressed.");
        }

        if (mandate.Provider != PaymentProviderType.PayFast || !mandate.IsActive || !mandate.IsReusable)
            return RecurringChargeResult.Failed("Mandate is not an active reusable PayFast mandate.");
        if (amountZar <= 0m)
            return RecurringChargeResult.Failed("Amount must be greater than zero.");

        string token;
        try
        {
            token = _protector.Unprotect(mandate.AuthorizationCodeProtected);
        }
        catch (Exception ex)
        {
            // Never log the token or the protected blob.
            _logger.LogError(ex,
                "[payment][payfast][adhoc] could not unprotect mandate {MandateId} for invoice {InvoiceId}.",
                mandate.Id, context.InvoiceId);
            return RecurringChargeResult.Failed("Stored PayFast token is unreadable; customer must re-authorize.");
        }

        var amountCents = (long)Math.Round(amountZar * 100m, MidpointRounding.AwayFromZero);

        var result = await _adhoc.ChargeAsync(token, amountCents, DefaultItemName, reference, cancellationToken);

        if (result.Accepted)
        {
            // Accepted ≠ settled. PayFast confirms via ITN; return Pending so
            // the engine leaves Payment/Initiation/Attempt pending and waits
            // for the ITN (handled by the existing PayFast notify handler).
            return RecurringChargeResult.Pending(
                providerTransactionId: result.ProviderTransactionId,
                message: "PayFast ad-hoc charge accepted; awaiting ITN settlement.",
                providerStatus: result.ProviderStatus);
        }

        return RecurringChargeResult.Failed(
            message: result.Message ?? "PayFast ad-hoc charge declined.",
            providerTransactionId: result.ProviderTransactionId,
            providerStatus: result.ProviderStatus);
    }
}

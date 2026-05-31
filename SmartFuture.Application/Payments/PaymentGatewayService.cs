using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Payments.Ozow;
using SmartFuture.Application.Payments.PayFast;
using SmartFuture.Application.Payments.Paystack;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Billing;
using Microsoft.Extensions.Hosting;
using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Payments;

public class PaymentGatewayService : IPaymentGatewayService
{
    private const string PaymentNumberPrefix = "PAY";
    private const int PaymentNumberMaxAttempts = 5;

    private readonly IAppDbContext _dbContext;
    private readonly IPaymentProviderRegistry _registry;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly OzowSettings _ozowSettings;
    private readonly PayFastSettings _payFastSettings;
    private readonly PaystackSettings _paystackSettings;
    private readonly IHostEnvironment _env;
    private readonly ILogger<PaymentGatewayService> _logger;

    public PaymentGatewayService(IAppDbContext dbContext, IPaymentProviderRegistry registry, IAuditService auditService, ICurrentUserService currentUser,
        IOptions<OzowSettings> ozowSettings, IOptions<PayFastSettings> payFastSettings, IOptions<PaystackSettings> paystackSettings,
        IHostEnvironment env, ILogger<PaymentGatewayService> logger)
    {
        _dbContext = dbContext;
        _registry = registry;
        _auditService = auditService;
        _currentUser = currentUser;
        _ozowSettings = ozowSettings.Value;
        _payFastSettings = payFastSettings.Value;
        _paystackSettings = paystackSettings.Value;
        _env = env;
        _logger = logger;
    }

    // Customer-facing availability gate. Returns null when the
    // provider is allowed for customer initiation; otherwise returns
    // the friendly error message to surface. Internal callers
    // (webhook handlers, admin tools, AutoBillingService) bypass this
    // by not going through InitiateInvoicePaymentAsync — the
    // initiators themselves remain registered + callable.
    private string? CheckCustomerProviderAvailable(PaymentProviderType provider)
    {
        var enabled = provider switch
        {
            PaymentProviderType.Paystack => _paystackSettings.Enabled,
            PaymentProviderType.Ozow     => _ozowSettings.Enabled,
            PaymentProviderType.PayFast  => _payFastSettings.Enabled,
            _ => true  // Manual / future providers not gated here.
        };
        if (enabled) return null;
        return "This payment method is temporarily unavailable. Please use Paystack.";
    }

    public async Task<Result<InitiateInvoicePaymentResultDto>> InitiateInvoicePaymentAsync(InitiateInvoicePaymentRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request is null)
                return Result<InitiateInvoicePaymentResultDto>.Failure(
                    ErrorCodes.BAD_REQUEST, "Request body is required.");

            if (request.InvoiceId == Guid.Empty)
                return Result<InitiateInvoicePaymentResultDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "InvoiceId is required.");

            var invoice = await _dbContext.Invoices
                .Include(i => i.Order)
                .FirstOrDefaultAsync(i => i.Id == request.InvoiceId, cancellationToken);

            if (invoice is null)
                return Result<InitiateInvoicePaymentResultDto>.Failure(
                    ErrorCodes.NOT_FOUND, "Invoice not found.");

            // Ownership check: customer can only initiate payment on their own invoice.
            // Admin context is also allowed via the same endpoint (admin invokes on customer's
            // behalf). When _currentUser.UserId is present, the invoice must belong to that user
            // unless the caller is admin; downstream authorisation policy is enforced at the
            // controller. We re-check here defensively.
            if (_currentUser.UserId.HasValue && invoice.Order is not null
                && invoice.Order.UserId != _currentUser.UserId.Value)
            {
                return Result<InitiateInvoicePaymentResultDto>.Failure(
                    ErrorCodes.FORBIDDEN, "Invoice does not belong to the current user.");
            }

            if (invoice.Status == InvoiceStatus.Paid
                || invoice.Status == InvoiceStatus.Void
                || invoice.Status == InvoiceStatus.Cancelled)
            {
                return Result<InitiateInvoicePaymentResultDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    $"Payments cannot be initiated for invoices in status '{invoice.Status}'.");
            }

            var amount = request.Amount ?? invoice.BalanceDue;

            if (amount <= 0)
                return Result<InitiateInvoicePaymentResultDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "Payment amount must be greater than zero.");

            if (amount > invoice.BalanceDue)
                return Result<InitiateInvoicePaymentResultDto>.Failure(
                    ErrorCodes.PAYMENT_AMOUNT_MISMATCH,
                    $"Payment amount ({amount:0.00}) exceeds invoice balance due ({invoice.BalanceDue:0.00}).");

            var initiator = _registry.GetInitiator(request.Provider);
            if (initiator is null)
            {
                return Result<InitiateInvoicePaymentResultDto>.Failure(
                    ErrorCodes.PROVIDER_NOT_CONFIGURED,
                    $"Payment provider '{request.Provider}' is not configured.");
            }

            // Customer-facing availability gate. Internal flows
            // (webhook apply, auto-billing charge_authorization, admin
            // tools) don't call through here, so disabling a provider
            // for customers leaves the rest of its pipeline intact.
            var availabilityError = CheckCustomerProviderAvailable(request.Provider);
            if (availabilityError is not null)
            {
                _logger.LogInformation(
                    "[PaymentGateway] customer-init refused for provider {Provider} (invoice {InvoiceId}) — provider Enabled flag is false.",
                    request.Provider, invoice.Id);
                return Result<InitiateInvoicePaymentResultDto>.Failure(
                    ErrorCodes.PROVIDER_NOT_CONFIGURED, availabilityError);
            }

            // ─── [OzowApiDebug] ────────────────────────────────────────
            // Diagnostic snapshot logged on every initiation. Surfaces
            // the user / invoice / payment numbers + the resolved Ozow
            // config (without secrets) so we can correlate a failing
            // payment against the env it was attempting.
            if (request.Provider == PaymentProviderType.Ozow)
            {
                _logger.LogInformation(
                    "[OzowApiDebug] invoiceId={InvoiceId} invoiceNumber={InvoiceNumber} userId={UserId} provider={Provider} " +
                    "invoiceTotal={InvoiceTotal} invoiceBalanceDue={BalanceDue} requestedAmount={Amount} " +
                    "invoiceStatus={InvoiceStatus} ozowIsConfigured={Configured} ozowIsTest={IsTest} " +
                    "ozowApiUrl={ApiUrl} ozowNotifyUrl={NotifyUrl} ozowSuccessUrl={SuccessUrl} " +
                    "ozowCancelUrl={CancelUrl} ozowErrorUrl={ErrorUrl} requestSuccessUrl={ReqSuccessUrl} " +
                    "requestCancelUrl={ReqCancelUrl} requestFailureUrl={ReqFailureUrl}",
                    invoice.Id, invoice.InvoiceNumber, _currentUser.UserId, request.Provider,
                    invoice.TotalAmount, invoice.BalanceDue, amount,
                    invoice.Status, _ozowSettings.IsConfigured, _ozowSettings.IsTest,
                    string.IsNullOrWhiteSpace(_ozowSettings.ApiUrl) ? "(default-live)" : _ozowSettings.ApiUrl,
                    _ozowSettings.NotifyUrl, _ozowSettings.SuccessUrl,
                    _ozowSettings.CancelUrl, _ozowSettings.ErrorUrl,
                    request.SuccessUrl, request.CancelUrl, request.FailureUrl);
            }

            var now = DateTime.UtcNow;

            var payment = new Payment
            {
                InvoiceId = invoice.Id,
                Status = PaymentStatus.Pending,
                Method = PaymentMethodType.Gateway,
                Amount = amount,
                CurrencyCode = invoice.CurrencyCode,
                GatewayName = request.Provider.ToString(),
                LastStatusChangedByUserId = _currentUser.UserId
            };

            var paymentNumber = await GenerateUniquePaymentNumberAsync(now, cancellationToken);
            if (paymentNumber is null)
                return Result<InitiateInvoicePaymentResultDto>.Failure(
                    ErrorCodes.EXCEPTION,
                    "Could not generate a unique payment number. Please retry.");
            payment.PaymentNumber = paymentNumber;

            _dbContext.Payments.Add(payment);
            await _dbContext.SaveChangesAsync(cancellationToken);

            // Call provider initiator. Provider failures become a Failed payment + a Failed
            // PaymentInitiation row — both persisted so admin can audit and re-try.
            PaymentProviderInitiationResult initiation;
            try
            {
                initiation = await initiator.InitiateAsync(invoice, payment, request, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Payment provider {Provider} threw during initiation for invoice {InvoiceId}",
                    request.Provider, invoice.Id);
                initiation = PaymentProviderInitiationResult.FailedResult(
                    $"Provider threw during initiation: {ex.Message}");
            }

            // Phase 1 — dry-run gating. Honoured only outside Production
            // so a misplaced flag can never silently strand a paid
            // customer with an unpaid invoice in prod. A blocked-in-prod
            // attempt logs a warning and proceeds with ApplyNormally.
            var applyMode = WebhookApplyMode.ApplyNormally;
            if (request.SuppressWebhookApplication == true)
            {
                if (_env.IsProduction())
                {
                    _logger.LogWarning(
                        "[PaymentInitiateDryRun] SuppressWebhookApplication=true was sent on Production for invoice {InvoiceId} — ignoring.",
                        invoice.Id);
                }
                else
                {
                    applyMode = WebhookApplyMode.ValidateOnly;
                    _logger.LogInformation(
                        "[PaymentInitiateDryRun] PaymentInitiation flagged ValidateOnly — webhook will validate but not apply. invoice={InvoiceId} env={Env}",
                        invoice.Id, _env.EnvironmentName);
                }
            }

            var paymentInitiation = new PaymentInitiation
            {
                InvoiceId = invoice.Id,
                PaymentId = payment.Id,
                Provider = request.Provider,
                // Mirror Payment.Amount — if the initiator applied the
                // test-amount override, both rows agree on what was
                // actually sent. The override audit fields below carry
                // the original invoice intent.
                Amount = payment.Amount,
                CurrencyCode = invoice.CurrencyCode,
                SuccessUrl = Trim(request.SuccessUrl),
                CancelUrl = Trim(request.CancelUrl),
                FailureUrl = Trim(request.FailureUrl),
                ProviderReference = Trim(initiation.ProviderReference),
                ProviderCheckoutId = Trim(initiation.ProviderCheckoutId),
                RedirectUrl = Trim(initiation.RedirectUrl),
                ExpiresAtUtc = initiation.ExpiresAtUtc,
                MetadataJson = initiation.MetadataJson,
                WebhookApplyMode = applyMode,
                IsTestAmountOverrideApplied = payment.IsTestAmountOverrideApplied,
                ActualProviderAmount = payment.ActualProviderAmount,
                InvoiceAmountAtTime = payment.InvoiceAmountAtTime
            };

            if (initiation.Success)
            {
                paymentInitiation.Status = !string.IsNullOrWhiteSpace(initiation.RedirectUrl)
                    ? PaymentInitiationStatus.RedirectRequired
                    : PaymentInitiationStatus.Pending;

                if (!string.IsNullOrWhiteSpace(initiation.ProviderReference))
                    payment.GatewayReference = initiation.ProviderReference;
                if (!string.IsNullOrWhiteSpace(initiation.ProviderCheckoutId))
                    payment.GatewayTransactionId = initiation.ProviderCheckoutId;
            }
            else
            {
                paymentInitiation.Status = PaymentInitiationStatus.Failed;
                paymentInitiation.FailureReason = Trim(initiation.FailureReason);

                payment.Status = PaymentStatus.Failed;
                payment.FailedAtUtc = now;
                payment.FailureReason = Trim(initiation.FailureReason);
            }

            _dbContext.PaymentInitiations.Add(paymentInitiation);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser.UserId,
                ActorType = _currentUser.UserId.HasValue ? AuditActorType.User : AuditActorType.System,
                ActionType = AuditActionType.PaymentStatusChanged,
                EntityType = AuditEntityType.Payment,
                EntityId = payment.Id,
                EntityName = payment.PaymentNumber,
                Summary = initiation.Success
                    ? $"Payment initiated via {request.Provider} ({payment.PaymentNumber})"
                    : $"Payment initiation failed via {request.Provider} ({payment.PaymentNumber})",
                MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    invoiceId = invoice.Id,
                    invoiceNumber = invoice.InvoiceNumber,
                    provider = request.Provider,
                    amount,
                    currencyCode = invoice.CurrencyCode,
                    initiationId = paymentInitiation.Id,
                    success = initiation.Success
                }),
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = initiation.Success
            });

            return Result<InitiateInvoicePaymentResultDto>.Success(new InitiateInvoicePaymentResultDto
            {
                Success = initiation.Success,
                Provider = request.Provider,
                PaymentId = payment.Id,
                PaymentInitiationId = paymentInitiation.Id,
                PaymentNumber = payment.PaymentNumber,
                ProviderReference = initiation.ProviderReference,
                RedirectUrl = initiation.RedirectUrl,
                FailureReason = initiation.FailureReason,
                // Phase 53.3 — propagate provider diagnostics so the
                // mobile/portal client can render the precise reason
                // a failure occurred (no need to grep logs).
                ProviderStatusCode       = initiation.ProviderStatusCode,
                ProviderErrorMessage     = initiation.ProviderErrorMessage,
                ProviderEndpoint         = initiation.ProviderEndpoint,
                ProviderIsTest           = initiation.ProviderIsTest,
                ProviderRawResponseSnippet = initiation.ProviderRawResponseSnippet,
                // Inline-checkout fields — only present when the
                // provider supports an embedded cashier flow
                // (currently Paystack). Portal uses these to open
                // the InlineJS overlay; mobile WebView + PayFast +
                // Ozow ignore them and use RedirectUrl instead.
                PaystackInline           = initiation.PaystackInline
            }, initiation.Success ? "Payment initiated." : "Payment initiation failed; details recorded.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error initiating invoice payment for {InvoiceId}", request?.InvoiceId);
            return Result<InitiateInvoicePaymentResultDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while initiating the payment.");
        }
    }

    public async Task<Result<PagedResult<PaymentInitiationDto>>> SearchAdminAsync(PaymentInitiationFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new PaymentInitiationFilterRequestDto();
            var query = _dbContext.PaymentInitiations
                .AsNoTracking()
                .Include(p => p.Invoice)
                .Include(p => p.Payment)
                .AsQueryable();

            if (filter.InvoiceId.HasValue)
                query = query.Where(p => p.InvoiceId == filter.InvoiceId.Value);
            if (filter.PaymentId.HasValue)
                query = query.Where(p => p.PaymentId == filter.PaymentId.Value);
            if (filter.Provider.HasValue)
                query = query.Where(p => p.Provider == filter.Provider.Value);
            if (filter.StatusFilter.HasValue)
                query = query.Where(p => p.Status == filter.StatusFilter.Value);

            if (!string.IsNullOrWhiteSpace(filter.ProviderReference))
            {
                var v = filter.ProviderReference.Trim();
                query = query.Where(p => p.ProviderReference != null && EF.Functions.Like(p.ProviderReference, $"%{v}%"));
            }

            if (!string.IsNullOrWhiteSpace(filter.ProviderCheckoutId))
            {
                var v = filter.ProviderCheckoutId.Trim();
                query = query.Where(p => p.ProviderCheckoutId != null && EF.Functions.Like(p.ProviderCheckoutId, $"%{v}%"));
            }

            if (filter.FromUtc.HasValue)
                query = query.Where(p => p.CreatedAtUtc >= filter.FromUtc.Value);
            if (filter.ToUtc.HasValue)
                query = query.Where(p => p.CreatedAtUtc <= filter.ToUtc.Value);

            var totalCount = await query.CountAsync(cancellationToken);

            var items = await query
                .OrderByDescending(p => p.CreatedAtUtc)
                .Skip((filter.Page - 1) * filter.PageSize)
                .Take(filter.PageSize)
                .ToListAsync(cancellationToken);

            var paged = new PagedResult<PaymentInitiationDto>(
                items.Select(MapToDto).ToList(),
                filter.Page,
                filter.PageSize,
                totalCount);

            return Result<PagedResult<PaymentInitiationDto>>.Success(paged);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching payment initiations");
            return Result<PagedResult<PaymentInitiationDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching payment initiations.");
        }
    }

    public async Task<Result<PaymentInitiationDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<PaymentInitiationDto>.Failure(ErrorCodes.BAD_REQUEST, "Initiation id is required.");

            var entity = await _dbContext.PaymentInitiations
                .AsNoTracking()
                .Include(p => p.Invoice)
                .Include(p => p.Payment)
                .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

            return entity is null
                ? Result<PaymentInitiationDto>.Failure(ErrorCodes.NOT_FOUND, "Payment initiation not found.")
                : Result<PaymentInitiationDto>.Success(MapToDto(entity));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching payment initiation {Id}", id);
            return Result<PaymentInitiationDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching the payment initiation.");
        }
    }

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

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PaymentInitiationDto MapToDto(PaymentInitiation p) => new()
    {
        Id = p.Id,
        InvoiceId = p.InvoiceId,
        InvoiceNumber = p.Invoice?.InvoiceNumber,
        PaymentId = p.PaymentId,
        PaymentNumber = p.Payment?.PaymentNumber,
        Provider = p.Provider,
        Status = p.Status,
        Amount = p.Amount,
        CurrencyCode = p.CurrencyCode,
        ProviderReference = p.ProviderReference,
        ProviderCheckoutId = p.ProviderCheckoutId,
        RedirectUrl = p.RedirectUrl,
        SuccessUrl = p.SuccessUrl,
        CancelUrl = p.CancelUrl,
        FailureUrl = p.FailureUrl,
        ExpiresAtUtc = p.ExpiresAtUtc,
        FailureReason = p.FailureReason,
        MetadataJson = p.MetadataJson,
        CreatedAtUtc = p.CreatedAtUtc,
        UpdatedAtUtc = p.UpdatedAtUtc
    };
}

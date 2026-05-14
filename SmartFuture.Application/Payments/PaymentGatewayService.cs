using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Billing;
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
    private readonly ILogger<PaymentGatewayService> _logger;

    public PaymentGatewayService(IAppDbContext dbContext, IPaymentProviderRegistry registry, IAuditService auditService, ICurrentUserService currentUser, ILogger<PaymentGatewayService> logger)
    {
        _dbContext = dbContext;
        _registry = registry;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
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

            var paymentInitiation = new PaymentInitiation
            {
                InvoiceId = invoice.Id,
                PaymentId = payment.Id,
                Provider = request.Provider,
                Amount = amount,
                CurrencyCode = invoice.CurrencyCode,
                SuccessUrl = Trim(request.SuccessUrl),
                CancelUrl = Trim(request.CancelUrl),
                FailureUrl = Trim(request.FailureUrl),
                ProviderReference = Trim(initiation.ProviderReference),
                ProviderCheckoutId = Trim(initiation.ProviderCheckoutId),
                RedirectUrl = Trim(initiation.RedirectUrl),
                ExpiresAtUtc = initiation.ExpiresAtUtc,
                MetadataJson = initiation.MetadataJson
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
                FailureReason = initiation.FailureReason
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

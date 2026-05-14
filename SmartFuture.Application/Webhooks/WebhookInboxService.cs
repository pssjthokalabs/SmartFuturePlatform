using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Application.Webhooks.Dtos;
using SmartFuture.Domain.Billing;
using SmartFuture.Domain.Orders;
using SmartFuture.Domain.Webhooks;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.Webhooks;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Webhooks;

public class WebhookInboxService : IWebhookInboxService
{
    private const int MaxProviderNameLength = 100;
    private const int MaxProviderEventIdLength = 200;
    private const int MaxSignatureHeaderLength = 500;
    private const int MaxIdempotencyKeyLength = 300;
    private const int MaxReferenceLength = 200;
    private const int MaxFailureReasonLength = 2000;

    private readonly IAppDbContext _dbContext;
    private readonly IWebhookSignatureValidator _signatureValidator;
    private readonly IWebhookPayloadParser _payloadParser;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly IPaymentApplierService _paymentApplier;
    private readonly ILogger<WebhookInboxService> _logger;

    public WebhookInboxService(IAppDbContext dbContext, IWebhookSignatureValidator signatureValidator, IWebhookPayloadParser payloadParser, IAuditService auditService, ICurrentUserService currentUser,
        IPaymentApplierService paymentApplier, ILogger<WebhookInboxService> logger)
    {
        _dbContext = dbContext;
        _signatureValidator = signatureValidator;
        _payloadParser = payloadParser;
        _auditService = auditService;
        _currentUser = currentUser;
        _paymentApplier = paymentApplier;
        _logger = logger;
    }

    public async Task<Result<WebhookInboxDto>> ReceivePaymentWebhookAsync(PaymentWebhookRequestDto request, CancellationToken cancellationToken = default)
    {
        if (request is null)
            return Result<WebhookInboxDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

        if (string.IsNullOrWhiteSpace(request.ProviderName))
            return Result<WebhookInboxDto>.Failure(ErrorCodes.VALIDATION_ERROR, "ProviderName is required.");

        if (string.IsNullOrWhiteSpace(request.RawPayload))
            return Result<WebhookInboxDto>.Failure(ErrorCodes.VALIDATION_ERROR, "RawPayload is required.");

        var rawHash = ComputeSha256(request.RawPayload);

        // Step 1: signature validation
        bool signatureValid;
        try
        {
            signatureValid = await _signatureValidator.IsValidAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Signature validator threw for provider {ProviderName}", request.ProviderName);
            signatureValid = false;
        }

        if (!signatureValid)
        {
            var rejected = await PersistInboxAsync(
                request,
                rawHash,
                eventType: WebhookEventType.Unknown,
                status: WebhookInboxStatus.SignatureInvalid,
                parsed: null,
                paymentId: null,
                invoiceId: null,
                failureReason: "Webhook signature was not valid for the supplied provider.",
                processedAtUtc: null,
                cancellationToken);

            return Result<WebhookInboxDto>.Failure(
                ErrorCodes.WEBHOOK_SIGNATURE_INVALID,
                "Webhook signature was invalid. Request not processed.");
        }

        // Step 2: parse payload
        ParsedPaymentWebhookDto? parsed = null;
        try
        {
            var parseResult = await _payloadParser.ParsePaymentWebhookAsync(request, cancellationToken);
            if (parseResult.IsSuccess) parsed = parseResult.Data;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Payload parser threw for provider {ProviderName}", request.ProviderName);
        }

        var effectiveEventId = !string.IsNullOrWhiteSpace(request.ProviderEventId)
            ? request.ProviderEventId
            : parsed?.ProviderEventId;

        var effectiveIdempotencyKey = !string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? request.IdempotencyKey
            : parsed?.IdempotencyKey;

        // Step 3: idempotency check
        WebhookInbox? duplicate = null;
        if (!string.IsNullOrWhiteSpace(effectiveEventId))
        {
            duplicate = await _dbContext.WebhookInboxes
                .AsNoTracking()
                .FirstOrDefaultAsync(w =>
                    w.ProviderName == request.ProviderName
                    && w.ProviderEventId == effectiveEventId, cancellationToken);
        }

        if (duplicate is null && !string.IsNullOrWhiteSpace(effectiveIdempotencyKey))
        {
            duplicate = await _dbContext.WebhookInboxes
                .AsNoTracking()
                .FirstOrDefaultAsync(w =>
                    w.ProviderName == request.ProviderName
                    && w.IdempotencyKey == effectiveIdempotencyKey, cancellationToken);
        }

        if (duplicate is not null)
        {
            _logger.LogInformation(
                "Duplicate webhook received for {ProviderName} (eventId={EventId}, idempotencyKey={Key}); original inbox id={InboxId}",
                request.ProviderName, effectiveEventId, effectiveIdempotencyKey, duplicate.Id);

            return Result<WebhookInboxDto>.Success(MapToDto(duplicate),
                "Duplicate webhook; original receipt returned.");
        }

        // Step 4: persist Received, attempt processing
        var eventType = parsed?.EventType ?? WebhookEventType.Unknown;

        var inbox = await PersistInboxAsync(
            request,
            rawHash,
            eventType,
            status: WebhookInboxStatus.Received,
            parsed,
            paymentId: null,
            invoiceId: null,
            failureReason: null,
            processedAtUtc: null,
            cancellationToken);

        if (parsed is null)
        {
            inbox.Status = WebhookInboxStatus.Failed;
            inbox.FailureReason = Truncate("Payload could not be parsed.", MaxFailureReasonLength);
            inbox.ProcessedAtUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Result<WebhookInboxDto>.Success(MapToDto(inbox),
                "Webhook recorded but payload could not be parsed.");
        }

        try
        {
            await ProcessParsedAsync(inbox, parsed, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Webhook processing threw for inbox {InboxId} (provider={Provider}, event={Event})",
                inbox.Id, inbox.ProviderName, inbox.EventType);
            inbox.Status = WebhookInboxStatus.Failed;
            inbox.FailureReason = Truncate($"Processing exception: {ex.Message}", MaxFailureReasonLength);
            inbox.ProcessedAtUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return Result<WebhookInboxDto>.Success(MapToDto(inbox),
            $"Webhook {inbox.Status.ToString().ToLowerInvariant()}.");
    }

    private async Task ProcessParsedAsync(WebhookInbox inbox, ParsedPaymentWebhookDto parsed, CancellationToken cancellationToken)
    {
        var payment = await FindMatchingPaymentAsync(parsed, cancellationToken);
        var invoice = payment?.Invoice
            ?? await FindMatchingInvoiceAsync(parsed, cancellationToken);

        if (payment is not null) inbox.PaymentId = payment.Id;
        if (invoice is not null) inbox.InvoiceId = invoice.Id;

        switch (parsed.EventType)
        {
            case WebhookEventType.PaymentCompleted:
                await HandlePaymentCompletedAsync(inbox, payment, invoice, cancellationToken);
                break;

            case WebhookEventType.PaymentFailed:
            case WebhookEventType.PaymentRefunded:
            case WebhookEventType.PaymentReversed:
            case WebhookEventType.PaymentCancelled:
            case WebhookEventType.PaymentPending:
                // Link only — automatic state changes for these events are deferred.
                // Admin can apply via PaymentService.AdminUpdateStatusAsync.
                if (payment is null)
                {
                    inbox.Status = WebhookInboxStatus.Ignored;
                    inbox.FailureReason = Truncate(
                        "No matching payment found; state change not applied.",
                        MaxFailureReasonLength);
                }
                else
                {
                    inbox.Status = WebhookInboxStatus.Processed;
                }
                inbox.ProcessedAtUtc = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);
                break;

            case WebhookEventType.MandateCreated:
            case WebhookEventType.MandateActivated:
            case WebhookEventType.MandateCancelled:
                // Mandate flows belong to a future system-safe debit-order applier.
                inbox.Status = WebhookInboxStatus.Ignored;
                inbox.FailureReason = Truncate(
                    "Mandate webhook recorded; automatic mandate state change deferred.",
                    MaxFailureReasonLength);
                inbox.ProcessedAtUtc = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);
                break;

            default:
                inbox.Status = WebhookInboxStatus.Ignored;
                inbox.FailureReason = Truncate(
                    $"Unsupported or unknown event type ({parsed.EventType}).",
                    MaxFailureReasonLength);
                inbox.ProcessedAtUtc = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);
                break;
        }
    }

    private async Task HandlePaymentCompletedAsync(WebhookInbox inbox, Payment? payment, Invoice? invoice, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        if (payment is null)
        {
            // No matching payment record — do NOT auto-create. Admin must reconcile.
            inbox.Status = WebhookInboxStatus.Ignored;
            inbox.FailureReason = Truncate(
                "PaymentCompleted received but no matching payment record found; admin reconciliation required.",
                MaxFailureReasonLength);
            inbox.ProcessedAtUtc = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        if (payment.Status == PaymentStatus.Completed)
        {
            // Already in target state — record and exit without double-applying arithmetic.
            inbox.Status = WebhookInboxStatus.Processed;
            inbox.FailureReason = Truncate(
                "Payment already in Completed state; no arithmetic applied.",
                MaxFailureReasonLength);
            inbox.ProcessedAtUtc = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        if (payment.Status != PaymentStatus.Pending)
        {
            // Conservative: only flip Pending → Completed automatically.
            inbox.Status = WebhookInboxStatus.Ignored;
            inbox.FailureReason = Truncate(
                $"Payment is in '{payment.Status}' state; auto-completion only supports 'Pending'. " +
                "Admin reconciliation required.",
                MaxFailureReasonLength);
            inbox.ProcessedAtUtc = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        // Persist the inbox state change first so the row is committed even if the applier
        // call below fails. PaymentApplierService runs its own transaction and emits its
        // own audit + notification, so we don't duplicate that here.
        inbox.Status = WebhookInboxStatus.Processed;
        inbox.ProcessedAtUtc = now;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var applyResult = await _paymentApplier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = payment.Id,
            NewStatus = PaymentStatus.Completed,
            PaidAtUtc = now,
            TriggerNotifications = true
        }, cancellationToken);

        if (!applyResult.IsSuccess)
        {
            // The applier already logged the underlying exception. Reflect the failure on
            // the inbox row so admins can spot it under /admin/inbox?Status=Failed.
            inbox.Status = WebhookInboxStatus.Failed;
            inbox.FailureReason = Truncate(
                $"Payment applier failed: {applyResult.Message}",
                MaxFailureReasonLength);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<Payment?> FindMatchingPaymentAsync(ParsedPaymentWebhookDto parsed, CancellationToken cancellationToken)
    {
        Payment? payment = null;

        if (!string.IsNullOrWhiteSpace(parsed.PaymentReference))
        {
            var pref = parsed.PaymentReference;
            payment = await _dbContext.Payments
                .Include(p => p.Invoice).ThenInclude(i => i!.Order)
                .FirstOrDefaultAsync(p =>
                    p.PaymentNumber == pref
                    || p.GatewayReference == pref
                    || p.GatewayTransactionId == pref
                    || p.ExternalReference == pref, cancellationToken);
        }

        if (payment is null && !string.IsNullOrWhiteSpace(parsed.GatewayTransactionId))
        {
            var tid = parsed.GatewayTransactionId;
            payment = await _dbContext.Payments
                .Include(p => p.Invoice).ThenInclude(i => i!.Order)
                .FirstOrDefaultAsync(p => p.GatewayTransactionId == tid, cancellationToken);
        }

        return payment;
    }

    private async Task<Invoice?> FindMatchingInvoiceAsync(ParsedPaymentWebhookDto parsed, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(parsed.InvoiceReference))
        {
            var iref = parsed.InvoiceReference;
            var invoice = await _dbContext.Invoices
                .Include(i => i.Order)
                .FirstOrDefaultAsync(i =>
                    i.InvoiceNumber == iref
                    || i.ExternalReference == iref, cancellationToken);
            if (invoice is not null) return invoice;
        }

        if (!string.IsNullOrWhiteSpace(parsed.OrderReference))
        {
            var oref = parsed.OrderReference;
            return await _dbContext.Invoices
                .Include(i => i.Order)
                .Where(i => i.Order != null && i.Order.OrderNumber == oref)
                .OrderByDescending(i => i.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return null;
    }

    private static void ApplyPaymentToInvoice(Invoice invoice, decimal deltaCompletedAmount, DateTime now)
    {
        invoice.AmountPaid += deltaCompletedAmount;
        if (invoice.AmountPaid < 0) invoice.AmountPaid = 0;

        invoice.BalanceDue = invoice.TotalAmount - invoice.AmountPaid;
        if (invoice.BalanceDue < 0) invoice.BalanceDue = 0;

        if (invoice.AmountPaid >= invoice.TotalAmount && invoice.TotalAmount > 0)
        {
            invoice.Status = InvoiceStatus.Paid;
            if (invoice.PaidAtUtc is null) invoice.PaidAtUtc = now;
        }
        else if (invoice.AmountPaid > 0)
        {
            invoice.Status = InvoiceStatus.PartiallyPaid;
            invoice.PaidAtUtc = null;
        }
    }

    private async Task<WebhookInbox> PersistInboxAsync(PaymentWebhookRequestDto request, string rawHash, WebhookEventType eventType, WebhookInboxStatus status, ParsedPaymentWebhookDto? parsed,
        Guid? paymentId, Guid? invoiceId, string? failureReason, DateTime? processedAtUtc, CancellationToken cancellationToken)
    {
        var inbox = new WebhookInbox
        {
            Provider = request.Provider,
            ProviderName = Truncate(request.ProviderName, MaxProviderNameLength) ?? string.Empty,
            ProviderEventId = Truncate(!string.IsNullOrWhiteSpace(request.ProviderEventId)
                ? request.ProviderEventId
                : parsed?.ProviderEventId, MaxProviderEventIdLength),
            EventType = eventType,
            Status = status,
            RawPayloadHash = rawHash,
            SignatureHeader = Truncate(request.SignatureHeader, MaxSignatureHeaderLength),
            IdempotencyKey = Truncate(!string.IsNullOrWhiteSpace(request.IdempotencyKey)
                ? request.IdempotencyKey
                : parsed?.IdempotencyKey, MaxIdempotencyKeyLength),
            PaymentReference = Truncate(parsed?.PaymentReference, MaxReferenceLength),
            InvoiceReference = Truncate(parsed?.InvoiceReference, MaxReferenceLength),
            OrderReference = Truncate(parsed?.OrderReference, MaxReferenceLength),
            GatewayTransactionId = Truncate(parsed?.GatewayTransactionId, MaxReferenceLength),
            Amount = parsed?.Amount,
            CurrencyCode = parsed?.CurrencyCode is { Length: > 0 } cc ? cc.Trim().ToUpperInvariant() : null,
            ProviderCreatedAtUtc = parsed?.ProviderCreatedAtUtc,
            ProcessedAtUtc = processedAtUtc,
            FailureReason = Truncate(failureReason, MaxFailureReasonLength),
            ParsedSummaryJson = parsed?.ParsedSummaryJson,
            PaymentId = paymentId,
            InvoiceId = invoiceId
        };

        if (inbox.CurrencyCode is not null && inbox.CurrencyCode.Length > 3)
            inbox.CurrencyCode = inbox.CurrencyCode[..3];

        _dbContext.WebhookInboxes.Add(inbox);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return inbox;
    }

    public async Task<Result<PagedResult<WebhookInboxDto>>> SearchAdminAsync(WebhookInboxFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new WebhookInboxFilterRequestDto();
            var query = _dbContext.WebhookInboxes.AsNoTracking().AsQueryable();

            if (filter.Provider.HasValue)
                query = query.Where(w => w.Provider == filter.Provider.Value);

            if (!string.IsNullOrWhiteSpace(filter.ProviderName))
            {
                var v = filter.ProviderName.Trim();
                query = query.Where(w => w.ProviderName == v);
            }

            if (filter.Status.HasValue)
                query = query.Where(w => w.Status == filter.Status.Value);

            if (filter.EventType.HasValue)
                query = query.Where(w => w.EventType == filter.EventType.Value);

            if (!string.IsNullOrWhiteSpace(filter.ProviderEventId))
            {
                var v = filter.ProviderEventId.Trim();
                query = query.Where(w => w.ProviderEventId == v);
            }

            if (!string.IsNullOrWhiteSpace(filter.IdempotencyKey))
            {
                var v = filter.IdempotencyKey.Trim();
                query = query.Where(w => w.IdempotencyKey == v);
            }

            if (!string.IsNullOrWhiteSpace(filter.PaymentReference))
            {
                var v = filter.PaymentReference.Trim();
                query = query.Where(w => w.PaymentReference != null && EF.Functions.Like(w.PaymentReference, $"%{v}%"));
            }

            if (!string.IsNullOrWhiteSpace(filter.InvoiceReference))
            {
                var v = filter.InvoiceReference.Trim();
                query = query.Where(w => w.InvoiceReference != null && EF.Functions.Like(w.InvoiceReference, $"%{v}%"));
            }

            if (!string.IsNullOrWhiteSpace(filter.OrderReference))
            {
                var v = filter.OrderReference.Trim();
                query = query.Where(w => w.OrderReference != null && EF.Functions.Like(w.OrderReference, $"%{v}%"));
            }

            if (!string.IsNullOrWhiteSpace(filter.GatewayTransactionId))
            {
                var v = filter.GatewayTransactionId.Trim();
                query = query.Where(w => w.GatewayTransactionId != null && EF.Functions.Like(w.GatewayTransactionId, $"%{v}%"));
            }

            if (filter.PaymentId.HasValue)
                query = query.Where(w => w.PaymentId == filter.PaymentId.Value);

            if (filter.InvoiceId.HasValue)
                query = query.Where(w => w.InvoiceId == filter.InvoiceId.Value);

            if (filter.ProcessedFromUtc.HasValue)
                query = query.Where(w => w.ProcessedAtUtc != null && w.ProcessedAtUtc >= filter.ProcessedFromUtc.Value);

            if (filter.ProcessedToUtc.HasValue)
                query = query.Where(w => w.ProcessedAtUtc != null && w.ProcessedAtUtc <= filter.ProcessedToUtc.Value);

            if (filter.FromUtc.HasValue)
                query = query.Where(w => w.CreatedAtUtc >= filter.FromUtc.Value);

            if (filter.ToUtc.HasValue)
                query = query.Where(w => w.CreatedAtUtc <= filter.ToUtc.Value);

            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var s = filter.Search.Trim();
                query = query.Where(w =>
                    EF.Functions.Like(w.ProviderName, $"%{s}%") ||
                    (w.ProviderEventId != null && EF.Functions.Like(w.ProviderEventId, $"%{s}%")) ||
                    (w.PaymentReference != null && EF.Functions.Like(w.PaymentReference, $"%{s}%")) ||
                    (w.InvoiceReference != null && EF.Functions.Like(w.InvoiceReference, $"%{s}%")) ||
                    (w.OrderReference != null && EF.Functions.Like(w.OrderReference, $"%{s}%")) ||
                    (w.GatewayTransactionId != null && EF.Functions.Like(w.GatewayTransactionId, $"%{s}%")));
            }

            var totalCount = await query.CountAsync(cancellationToken);

            var items = await query
                .OrderByDescending(w => w.CreatedAtUtc)
                .Skip((filter.Page - 1) * filter.PageSize)
                .Take(filter.PageSize)
                .ToListAsync(cancellationToken);

            var paged = new PagedResult<WebhookInboxDto>(
                items.Select(MapToDto).ToList(), filter.Page, filter.PageSize, totalCount);

            return Result<PagedResult<WebhookInboxDto>>.Success(paged);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching webhook inbox");
            return Result<PagedResult<WebhookInboxDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching webhook inbox.");
        }
    }

    public async Task<Result<WebhookInboxDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<WebhookInboxDto>.Failure(ErrorCodes.BAD_REQUEST, "Webhook id is required.");

            var entity = await _dbContext.WebhookInboxes
                .AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == id, cancellationToken);

            return entity is null
                ? Result<WebhookInboxDto>.Failure(ErrorCodes.NOT_FOUND, "Webhook inbox entry not found.")
                : Result<WebhookInboxDto>.Success(MapToDto(entity));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching webhook inbox {Id}", id);
            return Result<WebhookInboxDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching the webhook inbox entry.");
        }
    }

    private static string ComputeSha256(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max];
    }

    private static string? BuildMetadata(object payload)
    {
        try { return JsonSerializer.Serialize(payload); }
        catch { return null; }
    }

    private static WebhookInboxDto MapToDto(WebhookInbox w) => new()
    {
        Id = w.Id,
        Provider = w.Provider,
        ProviderName = w.ProviderName,
        ProviderEventId = w.ProviderEventId,
        EventType = w.EventType,
        Status = w.Status,
        RawPayloadHash = w.RawPayloadHash,
        IdempotencyKey = w.IdempotencyKey,
        PaymentReference = w.PaymentReference,
        InvoiceReference = w.InvoiceReference,
        OrderReference = w.OrderReference,
        GatewayTransactionId = w.GatewayTransactionId,
        Amount = w.Amount,
        CurrencyCode = w.CurrencyCode,
        ProviderCreatedAtUtc = w.ProviderCreatedAtUtc,
        ProcessedAtUtc = w.ProcessedAtUtc,
        FailureReason = w.FailureReason,
        ParsedSummaryJson = w.ParsedSummaryJson,
        PaymentId = w.PaymentId,
        InvoiceId = w.InvoiceId,
        CreatedAtUtc = w.CreatedAtUtc,
        UpdatedAtUtc = w.UpdatedAtUtc
    };
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.OrderIntents;
using SmartFuture.Application.Persistence;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.OrderIntents;
using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Payments.Paystack;

/// <summary>
/// Read-only counterpart to <see cref="IPaystackReconciliationService"/>.
///
/// `verify-and-apply` (POST) is the right tool when the customer returns
/// from Paystack and we need to force a fresh <c>/transaction/verify</c>
/// against Paystack to close the loop. It's idempotent but it always
/// makes the upstream call — which is wasted work (and Paystack rate
/// budget) when the mobile app is polling at 3s intervals waiting for
/// our webhook + apply to land.
///
/// This service ANSWERS THE QUESTION "what does the SmartFuture DB
/// currently say about this reference" — no Paystack call, no state
/// mutation, no audit log. The mobile + portal result screens call this
/// on a short loop after the first <c>verify-and-apply</c> to see when
/// the apply path has finished and the Order has been created. It's
/// safe to expose anonymously: the reference is the auth, no PII is
/// returned, and the response is a pure read of our own tables.
/// </summary>
public interface IPaystackStatusService
{
    Task<Result<PaystackStatusOutcomeDto>> GetStatusAsync(string reference, CancellationToken cancellationToken = default);
}

public class PaystackStatusService : IPaystackStatusService
{
    private readonly IAppDbContext _dbContext;
    private readonly ILogger<PaystackStatusService> _logger;

    public PaystackStatusService(IAppDbContext dbContext, ILogger<PaystackStatusService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<Result<PaystackStatusOutcomeDto>> GetStatusAsync(string reference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return Result<PaystackStatusOutcomeDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Reference is required.");

        reference = reference.Trim();
        var outcome = new PaystackStatusOutcomeDto { Reference = reference };

        // SF-INTENT-* references → look at the OrderIntent + (if converted)
        // its linked Order/Invoice/Payment. Status mapping:
        //
        //   intent.Status = ConvertedToOrder
        //     → invoice present + Paid + order present → "success"
        //     → invoice present + Paid + no order      → "order-finalization-pending"
        //   intent.Status = Cancelled                  → "cancelled"
        //   intent.Status = Expired                    → "failed"
        //   payment row exists + status = Completed    → "success"
        //   payment row exists + status = Failed       → "failed"
        //   otherwise                                  → "pending"
        if (OrderIntentService.IsIntentReference(reference))
        {
            var intent = await _dbContext.OrderIntents
                .AsNoTracking()
                .FirstOrDefaultAsync(i => i.IntentPaymentReference == reference, cancellationToken);
            if (intent is null)
            {
                outcome.LocalStatus = "not-found";
                return Result<PaystackStatusOutcomeDto>.Success(outcome, "No intent matches that reference yet.");
            }

            outcome.IntentStatus = intent.Status.ToString();
            outcome.OrderIntentId = intent.Id;
            outcome.IntentToken = intent.IntentToken;

            if (intent.Status == OrderIntentStatus.ConvertedToOrder && intent.ConvertedOrderId is Guid orderId)
            {
                var order = await _dbContext.Orders.AsNoTracking()
                    .Where(o => o.Id == orderId)
                    .Select(o => new { o.Id, o.OrderNumber })
                    .FirstOrDefaultAsync(cancellationToken);
                var invoice = await _dbContext.Invoices.AsNoTracking()
                    .Where(i => i.OrderId == orderId)
                    .OrderByDescending(i => i.CreatedAtUtc)
                    .Select(i => new { i.Id, i.InvoiceNumber, i.Status, i.TotalAmount })
                    .FirstOrDefaultAsync(cancellationToken);
                var payment = invoice is null ? null : await _dbContext.Payments.AsNoTracking()
                    .Where(p => p.InvoiceId == invoice.Id)
                    .OrderByDescending(p => p.CreatedAtUtc)
                    .Select(p => new { p.Id, p.PaymentNumber, p.Status })
                    .FirstOrDefaultAsync(cancellationToken);

                outcome.OrderId = order?.Id;
                outcome.OrderNumber = order?.OrderNumber;
                outcome.InvoiceId = invoice?.Id;
                outcome.InvoiceNumber = invoice?.InvoiceNumber;
                outcome.InvoiceStatus = invoice?.Status.ToString();
                outcome.InvoiceAmount = invoice?.TotalAmount;
                outcome.PaymentId = payment?.Id;
                outcome.PaymentNumber = payment?.PaymentNumber;
                outcome.PaymentStatus = payment?.Status.ToString();

                if (order is not null
                    && invoice is not null
                    && string.Equals(invoice.Status.ToString(), "Paid", StringComparison.OrdinalIgnoreCase))
                {
                    outcome.LocalStatus = "success";
                    return Result<PaystackStatusOutcomeDto>.Success(outcome, "Order created and invoice paid.");
                }

                // Conversion is mid-flight or partially complete — the
                // applier may have failed and an admin will reconcile.
                outcome.LocalStatus = "order-finalization-pending";
                return Result<PaystackStatusOutcomeDto>.Success(outcome,
                    "Intent is marked converted but the order/invoice rows aren't fully ready yet.");
            }

            if (intent.Status == OrderIntentStatus.Cancelled)
            {
                outcome.LocalStatus = "cancelled";
                return Result<PaystackStatusOutcomeDto>.Success(outcome, "OrderIntent was cancelled.");
            }
            if (intent.Status == OrderIntentStatus.Expired)
            {
                outcome.LocalStatus = "failed";
                return Result<PaystackStatusOutcomeDto>.Success(outcome, "OrderIntent expired before payment.");
            }

            outcome.LocalStatus = "pending";
            return Result<PaystackStatusOutcomeDto>.Success(outcome, "Awaiting Paystack confirmation.");
        }

        // SF-PAY-* and ad-hoc invoice references → look at the
        // PaymentInitiation + linked Invoice/Payment.
        var initiation = await _dbContext.PaymentInitiations.AsNoTracking()
            .Include(i => i.Invoice)
            .Include(i => i.Payment)
            .FirstOrDefaultAsync(i => i.Provider == PaymentProviderType.Paystack
                                    && i.ProviderReference == reference,
                                  cancellationToken);
        if (initiation is null)
        {
            outcome.LocalStatus = "not-found";
            return Result<PaystackStatusOutcomeDto>.Success(outcome, "No initiation matches that reference yet.");
        }

        outcome.InvoiceId = initiation.InvoiceId;
        outcome.InvoiceNumber = initiation.Invoice?.InvoiceNumber;
        outcome.InvoiceStatus = initiation.Invoice?.Status.ToString();
        outcome.InvoiceAmount = initiation.Invoice?.TotalAmount;
        outcome.PaymentId = initiation.PaymentId;
        outcome.PaymentNumber = initiation.Payment?.PaymentNumber;
        outcome.PaymentStatus = initiation.Payment?.Status.ToString();

        if (initiation.Payment is not null && initiation.Payment.Status == PaymentStatus.Completed)
        {
            outcome.LocalStatus = "success";
            return Result<PaystackStatusOutcomeDto>.Success(outcome, "Payment is completed.");
        }
        if (initiation.Payment is not null && initiation.Payment.Status == PaymentStatus.Failed)
        {
            outcome.LocalStatus = "failed";
            return Result<PaystackStatusOutcomeDto>.Success(outcome, "Payment failed.");
        }

        outcome.LocalStatus = "pending";
        return Result<PaystackStatusOutcomeDto>.Success(outcome, "Awaiting Paystack confirmation.");
    }
}

public class PaystackStatusOutcomeDto
{
    public string Reference { get; set; } = string.Empty;

    /// <summary>
    /// Resolved local status. Mobile + portal switch on this:
    ///   "success", "failed", "cancelled", "pending",
    ///   "order-finalization-pending" (intent converted but order/invoice
    ///   row isn't fully ready), "not-found" (reference unknown — may
    ///   simply mean the webhook hasn't landed yet).
    /// </summary>
    public string? LocalStatus { get; set; }

    public Guid?   OrderIntentId { get; set; }
    public string? IntentStatus  { get; set; }
    public string? IntentToken   { get; set; }

    public Guid?   OrderId       { get; set; }
    public string? OrderNumber   { get; set; }

    public Guid?    InvoiceId     { get; set; }
    public string?  InvoiceNumber { get; set; }
    public string?  InvoiceStatus { get; set; }
    public decimal? InvoiceAmount { get; set; }

    public Guid?   PaymentId     { get; set; }
    public string? PaymentNumber { get; set; }
    public string? PaymentStatus { get; set; }
}

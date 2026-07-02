using SmartFuture.Application.Billing;
using SmartFuture.Domain.Billing;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Application.Billing.ProRata;

/// <summary>
/// Stateless helper that constructs the first pro-rata invoice raised
/// when admin marks a Fibre-type service Active. Extracted from
/// <c>OrderService.TryGenerateFibreProRataInvoiceAsync</c> so the
/// money-shaping logic (idempotency guard, product-type branch, quote
/// call, invoice + line-item shape) is unit-testable without loading
/// the full <c>OrderService</c> DI graph.
///
/// Contract (mirrors the private method it replaced):
///   • Returns <c>null</c> when the order was already invoiced for its
///     first pro-rata (idempotency stamp on the Order).
///   • Returns <c>null</c> when the product line pays pro-rata at
///     CHECKOUT rather than activation — Security today, driven by
///     <c>BillingSettings.ChargeProRataAtCheckout(pkgType)</c>.
///   • Returns <c>null</c> when the customer activated on their billing
///     day (billable days = 0).
///   • Otherwise: returns a fresh <see cref="Invoice"/> (with the single
///     ProRata line item attached) plus the <c>issuedAtUtc</c> stamp the
///     caller should copy onto <c>Order.FirstProRataInvoiceGeneratedAtUtc</c>.
///
/// The caller adds the invoice via <c>IAppDbContext.Invoices.Add(...)</c>
/// and calls <c>SaveChangesAsync</c>. Nothing here touches the DbContext,
/// which is why the whole shape can be exercised in an isolated test.
/// </summary>
public static class FibreActivationProRataFactory
{
    public sealed record Result(Invoice Invoice, DateTime IssuedAtUtc);

    public static Result? TryBuild(
        Order order,
        DateTime activationDateUtc,
        DateTime nowUtc,
        BillingSettings billingSettings,
        Guid? actingUserId)
    {
        // Idempotency stamp — already invoiced this order's first
        // pro-rata (either here on a prior activation, or at intent
        // conversion for a Security package).
        if (order.FirstProRataInvoiceGeneratedAtUtc.HasValue) return null;

        // Only run for product lines where the service fee starts AFTER
        // activation. Security packages had pro-rata included at checkout.
        if (billingSettings.ChargeProRataAtCheckout(order.PackageType)) return null;

        var quote = ProRataCalculator.Quote(order.PackagePrice, activationDateUtc, order.PreferredBillingDay);
        if (quote.BillableDays <= 0 || quote.ProRataAmount <= 0m) return null; // customer activated on billing day

        var stamp = nowUtc.ToString("yyyyMMdd");
        var shortId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var invoice = new Invoice
        {
            InvoiceNumber = $"INV-{stamp}-{shortId}",
            OrderId = order.Id,
            Status = InvoiceStatus.Issued,
            SubtotalAmount = quote.ProRataAmount,
            TotalAmount = quote.ProRataAmount,
            BalanceDue = quote.ProRataAmount,
            AmountPaid = 0m,
            CurrencyCode = "ZAR",
            IssuedAtUtc = nowUtc,
            DueAtUtc = quote.NextBillingDateUtc, // due on customer's next billing day
            PeriodStartUtc = quote.StartDateUtc,
            PeriodEndUtc = quote.NextBillingDateUtc,
            LastStatusChangedByUserId = actingUserId,
            Notes = "First pro-rata invoice generated on service activation.",
        };
        invoice.LineItems.Add(new InvoiceLineItem
        {
            Invoice = invoice,
            LineType = InvoiceLineItemType.ProRata,
            Description = ProRataCalculator.FormatProRataDescription(quote.StartDateUtc, quote.NextBillingDateUtc),
            Quantity = 1,
            UnitAmount = quote.ProRataAmount,
            TotalAmount = quote.ProRataAmount,
            SortOrder = 0,
        });

        return new Result(invoice, nowUtc);
    }
}

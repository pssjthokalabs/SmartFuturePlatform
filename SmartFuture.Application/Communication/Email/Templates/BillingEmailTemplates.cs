using System.Text;
using SmartFuture.Shared.Enums.Communication;

namespace SmartFuture.Application.Communication.Email.Templates;

/// <summary>
/// Billing / payment templates. Payment receipts are sent from
/// <see cref="EmailSenderType.Payments"/>; standalone invoice notices
/// from <see cref="EmailSenderType.Accounts"/>.
///
/// **Mock checkout note**: in UAT the order-create flow persists a
/// Paid invoice and a Completed Ozow payment when
/// <c>PaymentSettings:MockCheckoutEnabled=true</c>. The
/// <see cref="PaymentReceived"/> template is the primary confirmation
/// for that flow so customers don't get an invoice email AND a
/// payment email for the same checkout — it bundles both.
/// </summary>
public static class BillingEmailTemplates
{
    public class PaymentReceivedModel
    {
        public string CustomerFirstName { get; set; } = string.Empty;
        public string PaymentNumber { get; set; } = string.Empty;
        public string? InvoiceNumber { get; set; }
        public string? OrderNumber { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "ZAR";
        public string? Method { get; set; }           // e.g. "Gateway", "Ozow"
        public string? GatewayName { get; set; }
        public string? GatewayReference { get; set; }
        public DateTime? PaidAtUtc { get; set; }
        public string? PackageName { get; set; }
        public string? InvoiceDetailUrl { get; set; }
        public string? PaymentDetailUrl { get; set; }
        // Free-form hint shown above the summary. Examples:
        //   "We've received your payment for your Smart Future order."
        //   "Your Ozow payment has been received."
        public string? IntroSentence { get; set; }
    }

    public static SmartFutureEmailContent PaymentReceived(PaymentReceivedModel m)
    {
        var greeting = string.IsNullOrWhiteSpace(m.CustomerFirstName) ? "there" : m.CustomerFirstName;
        var subject = string.IsNullOrWhiteSpace(m.OrderNumber)
            ? $"Payment received: {m.PaymentNumber}"
            : $"Payment received for order {m.OrderNumber}";

        var intro = string.IsNullOrWhiteSpace(m.IntroSentence)
            ? "We've received your payment. Here's a summary for your records:"
            : m.IntroSentence!;

        var summary = new List<(string Label, string Value)>
        {
            ("Payment number", m.PaymentNumber),
            ("Invoice", m.InvoiceNumber ?? string.Empty),
            ("Order", m.OrderNumber ?? string.Empty),
            ("Package", m.PackageName ?? string.Empty),
            ("Amount", FormatCurrency(m.Amount, m.Currency)),
            ("Method", string.IsNullOrWhiteSpace(m.GatewayName) ? (m.Method ?? "—") : m.GatewayName!),
            ("Gateway reference", m.GatewayReference ?? string.Empty),
            ("Paid", m.PaidAtUtc?.ToString("dd MMM yyyy") ?? string.Empty),
        };

        var inner = new StringBuilder();
        inner.Append(SmartFutureEmailLayout.Heading("Payment received"));
        inner.Append(SmartFutureEmailLayout.Paragraph($"Hi {greeting},"));
        inner.Append(SmartFutureEmailLayout.Paragraph(intro));
        inner.Append(SmartFutureEmailLayout.KeyValueTable(summary));

        if (!string.IsNullOrWhiteSpace(m.InvoiceDetailUrl))
        {
            inner.Append(SmartFutureEmailLayout.CallToActionButton(m.InvoiceDetailUrl, "View invoice"));
        }
        else if (!string.IsNullOrWhiteSpace(m.PaymentDetailUrl))
        {
            inner.Append(SmartFutureEmailLayout.CallToActionButton(m.PaymentDetailUrl, "View payment"));
        }

        inner.Append(SmartFutureEmailLayout.Divider());
        inner.Append(SmartFutureEmailLayout.MutedNote(
            "Service activation is handled separately. Our installation team will be in touch."));

        var html = SmartFutureEmailLayout.Compose(
            title: subject,
            preheader: $"Payment {m.PaymentNumber} — {FormatCurrency(m.Amount, m.Currency)} received.",
            innerHtml: inner.ToString());

        var plain = new StringBuilder()
            .AppendLine($"Hi {greeting},")
            .AppendLine()
            .AppendLine(intro)
            .AppendLine()
            .AppendLine($"Payment number: {m.PaymentNumber}")
            .AppendLine($"Invoice:        {m.InvoiceNumber ?? "—"}")
            .AppendLine($"Order:          {m.OrderNumber ?? "—"}")
            .AppendLine($"Package:        {m.PackageName ?? "—"}")
            .AppendLine($"Amount:         {FormatCurrency(m.Amount, m.Currency)}")
            .AppendLine($"Method:         {(string.IsNullOrWhiteSpace(m.GatewayName) ? (m.Method ?? "—") : m.GatewayName)}")
            .AppendLine($"Gateway ref:    {m.GatewayReference ?? "—"}")
            .AppendLine($"Paid:           {m.PaidAtUtc?.ToString("dd MMM yyyy") ?? "—"}")
            .AppendLine()
            .AppendLine(m.InvoiceDetailUrl is not null ? $"View invoice: {m.InvoiceDetailUrl}" : string.Empty)
            .AppendLine(m.PaymentDetailUrl is not null ? $"View payment: {m.PaymentDetailUrl}" : string.Empty)
            .AppendLine()
            .AppendLine("Service activation is handled separately. Our installation team will be in touch.")
            .AppendLine()
            .AppendLine("— The Smart Future team")
            .ToString();

        return new SmartFutureEmailContent(EmailSenderType.Payments, subject, html, plain);
    }

    public class InvoiceIssuedModel
    {
        public string CustomerFirstName { get; set; } = string.Empty;
        public string InvoiceNumber { get; set; } = string.Empty;
        public string? OrderNumber { get; set; }
        public decimal Amount { get; set; }
        public decimal BalanceDue { get; set; }
        public string Currency { get; set; } = "ZAR";
        public DateTime? IssuedAtUtc { get; set; }
        public DateTime? DueAtUtc { get; set; }
        public string? InvoiceDetailUrl { get; set; }
    }

    /// <summary>
    /// Invoice-issued template for the future "admin issues an
    /// unpaid invoice" flow. Not wired yet (mock checkout uses
    /// <see cref="PaymentReceived"/>); kept so it's available when
    /// admin-initiated invoice creation goes live.
    /// </summary>
    public static SmartFutureEmailContent InvoiceIssued(InvoiceIssuedModel m)
    {
        var greeting = string.IsNullOrWhiteSpace(m.CustomerFirstName) ? "there" : m.CustomerFirstName;
        var subject = $"New invoice from Smart Future: {m.InvoiceNumber}";

        var rows = new List<(string Label, string Value)>
        {
            ("Invoice number", m.InvoiceNumber),
            ("Order", m.OrderNumber ?? string.Empty),
            ("Total", FormatCurrency(m.Amount, m.Currency)),
            ("Balance due", FormatCurrency(m.BalanceDue, m.Currency)),
            ("Issued", m.IssuedAtUtc?.ToString("dd MMM yyyy") ?? string.Empty),
            ("Due", m.DueAtUtc?.ToString("dd MMM yyyy") ?? string.Empty),
        };

        var inner = new StringBuilder();
        inner.Append(SmartFutureEmailLayout.Heading("New invoice"));
        inner.Append(SmartFutureEmailLayout.Paragraph($"Hi {greeting},"));
        inner.Append(SmartFutureEmailLayout.Paragraph("A new invoice has been issued for your Smart Future account:"));
        inner.Append(SmartFutureEmailLayout.KeyValueTable(rows));
        if (!string.IsNullOrWhiteSpace(m.InvoiceDetailUrl))
        {
            inner.Append(SmartFutureEmailLayout.CallToActionButton(m.InvoiceDetailUrl, "View invoice"));
        }

        var html = SmartFutureEmailLayout.Compose(
            title: subject,
            preheader: $"Invoice {m.InvoiceNumber} — {FormatCurrency(m.BalanceDue, m.Currency)} due.",
            innerHtml: inner.ToString());

        var plain = new StringBuilder()
            .AppendLine($"Hi {greeting},")
            .AppendLine()
            .AppendLine("A new invoice has been issued for your Smart Future account.")
            .AppendLine()
            .AppendLine($"Invoice number: {m.InvoiceNumber}")
            .AppendLine($"Order:          {m.OrderNumber ?? "—"}")
            .AppendLine($"Total:          {FormatCurrency(m.Amount, m.Currency)}")
            .AppendLine($"Balance due:    {FormatCurrency(m.BalanceDue, m.Currency)}")
            .AppendLine($"Issued:         {m.IssuedAtUtc?.ToString("dd MMM yyyy") ?? "—"}")
            .AppendLine($"Due:            {m.DueAtUtc?.ToString("dd MMM yyyy") ?? "—"}")
            .AppendLine()
            .AppendLine(m.InvoiceDetailUrl is not null ? $"View invoice: {m.InvoiceDetailUrl}" : string.Empty)
            .AppendLine()
            .AppendLine("— The Smart Future team")
            .ToString();

        return new SmartFutureEmailContent(EmailSenderType.Accounts, subject, html, plain);
    }

    private static string FormatCurrency(decimal amount, string currency)
    {
        var prefix = string.Equals(currency, "ZAR", StringComparison.OrdinalIgnoreCase) ? "R" : currency;
        return $"{prefix} {amount:N2}";
    }
}

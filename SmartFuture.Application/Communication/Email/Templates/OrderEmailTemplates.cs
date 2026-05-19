using System.Globalization;
using System.Text;
using SmartFuture.Shared.Enums.Communication;

namespace SmartFuture.Application.Communication.Email.Templates;

/// <summary>
/// Order-flow email templates. Default sender is
/// <see cref="EmailSenderType.NoReply"/> — order confirmations are
/// transactional and don't expect replies.
/// </summary>
public static class OrderEmailTemplates
{
    public class OrderSubmittedModel
    {
        public string CustomerFirstName { get; set; } = string.Empty;
        public string CustomerFullName { get; set; } = string.Empty;
        public string OrderNumber { get; set; } = string.Empty;
        public string PackageName { get; set; } = string.Empty;
        public string? SpeedLabel { get; set; }
        public string? DataAllowanceLabel { get; set; }
        public bool IsUncapped { get; set; }
        public decimal PackagePrice { get; set; }
        public decimal? InstallationFee { get; set; }
        public bool HasFreeInstallation { get; set; }
        public string AddressLine1 { get; set; } = string.Empty;
        public string? Suburb { get; set; }
        public string? City { get; set; }
        public string? Province { get; set; }
        public string? PostalCode { get; set; }
        public string OrderStatusLabel { get; set; } = "Submitted";

        // Optional payment summary (populated when mock checkout
        // recorded a payment at order-create time). When null the
        // template renders the order without payment details.
        public string? PaymentProvider { get; set; }
        public string? PaymentReference { get; set; }
        public decimal? PaymentAmount { get; set; }

        public string? OrderDetailUrl { get; set; }
    }

    public static SmartFutureEmailContent OrderSubmitted(OrderSubmittedModel m)
    {
        var greeting = string.IsNullOrWhiteSpace(m.CustomerFirstName) ? "there" : m.CustomerFirstName;
        var subject = $"Order received: {m.OrderNumber}";

        var packageRows = new List<(string Label, string Value)>
        {
            ("Order number", m.OrderNumber),
            ("Package", m.PackageName),
            ("Speed", string.IsNullOrWhiteSpace(m.SpeedLabel) ? "—" : m.SpeedLabel!),
            ("Data", BuildDataLine(m)),
            ("Status", m.OrderStatusLabel),
            ("Monthly price", FormatZar(m.PackagePrice)),
            ("Installation fee", BuildInstallationFee(m)),
        };

        var addressRows = new List<(string Label, string Value)>
        {
            ("Address", m.AddressLine1),
            ("Suburb", m.Suburb ?? string.Empty),
            ("City", m.City ?? string.Empty),
            ("Province", m.Province ?? string.Empty),
            ("Postal code", m.PostalCode ?? string.Empty),
        };

        var hasPayment = !string.IsNullOrWhiteSpace(m.PaymentReference) || m.PaymentAmount.HasValue;
        var paymentRows = hasPayment ? new List<(string Label, string Value)>
        {
            ("Payment provider", m.PaymentProvider ?? "—"),
            ("Payment reference", m.PaymentReference ?? "—"),
            ("Amount", m.PaymentAmount.HasValue ? FormatZar(m.PaymentAmount.Value) : "—"),
        } : null;

        var innerSb = new StringBuilder();
        innerSb.Append(SmartFutureEmailLayout.Heading("Thanks for your order"));
        innerSb.Append(SmartFutureEmailLayout.Paragraph($"Hi {greeting},"));
        innerSb.Append(SmartFutureEmailLayout.Paragraph(
            $"We've received your Smart Future order. Our team will be in touch with the next steps shortly. " +
            $"Here is a summary for your records:"));

        innerSb.Append(SmartFutureEmailLayout.SubHeading("Package"));
        innerSb.Append(SmartFutureEmailLayout.KeyValueTable(packageRows));

        innerSb.Append(SmartFutureEmailLayout.SubHeading("Service address"));
        innerSb.Append(SmartFutureEmailLayout.KeyValueTable(addressRows));

        if (paymentRows is not null)
        {
            innerSb.Append(SmartFutureEmailLayout.SubHeading("Payment"));
            innerSb.Append(SmartFutureEmailLayout.KeyValueTable(paymentRows));
        }

        if (!string.IsNullOrWhiteSpace(m.OrderDetailUrl))
        {
            innerSb.Append(SmartFutureEmailLayout.CallToActionButton(m.OrderDetailUrl, "View in Client Zone"));
        }

        innerSb.Append(SmartFutureEmailLayout.Divider());
        innerSb.Append(SmartFutureEmailLayout.MutedNote(
            "Your service stays inactive until our installation team confirms it. " +
            "We'll send another email when there's an update."));

        var html = SmartFutureEmailLayout.Compose(
            title: subject,
            preheader: $"Order {m.OrderNumber} — {m.PackageName} — we'll be in touch shortly.",
            innerHtml: innerSb.ToString());

        var plain = new StringBuilder()
            .AppendLine($"Hi {greeting},")
            .AppendLine()
            .AppendLine($"Thank you for your order with Smart Future.")
            .AppendLine()
            .AppendLine($"Order number: {m.OrderNumber}")
            .AppendLine($"Package: {m.PackageName}")
            .AppendLine($"Speed: {(string.IsNullOrWhiteSpace(m.SpeedLabel) ? "—" : m.SpeedLabel)}")
            .AppendLine($"Status: {m.OrderStatusLabel}")
            .AppendLine($"Monthly: {FormatZar(m.PackagePrice)}")
            .AppendLine($"Installation: {BuildInstallationFee(m)}")
            .AppendLine()
            .AppendLine("Service address:")
            .AppendLine(BuildAddressText(m))
            .AppendLine();

        if (hasPayment)
        {
            plain.AppendLine("Payment:");
            plain.AppendLine($"  Provider:  {m.PaymentProvider ?? "—"}");
            plain.AppendLine($"  Reference: {m.PaymentReference ?? "—"}");
            if (m.PaymentAmount.HasValue) plain.AppendLine($"  Amount:    {FormatZar(m.PaymentAmount.Value)}");
            plain.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(m.OrderDetailUrl))
        {
            plain.AppendLine($"View in Client Zone: {m.OrderDetailUrl}");
            plain.AppendLine();
        }

        plain.AppendLine("Your service stays inactive until our installation team confirms it.");
        plain.AppendLine();
        plain.AppendLine("— The Smart Future team");

        return new SmartFutureEmailContent(EmailSenderType.NoReply, subject, html, plain.ToString());
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static string BuildDataLine(OrderSubmittedModel m)
    {
        if (m.IsUncapped) return "Uncapped";
        return string.IsNullOrWhiteSpace(m.DataAllowanceLabel) ? "—" : m.DataAllowanceLabel!;
    }

    private static string BuildInstallationFee(OrderSubmittedModel m)
    {
        if (m.HasFreeInstallation) return "Free";
        if (m.InstallationFee is null || m.InstallationFee.Value == 0m) return "Free";
        return FormatZar(m.InstallationFee.Value);
    }

    private static string BuildAddressText(OrderSubmittedModel m)
    {
        var parts = new[] { m.AddressLine1, m.Suburb, m.City, m.Province, m.PostalCode }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join(", ", parts);
    }

    internal static string FormatZar(decimal amount)
        => "R " + amount.ToString("N2", CultureInfo.GetCultureInfo("en-ZA"));
}

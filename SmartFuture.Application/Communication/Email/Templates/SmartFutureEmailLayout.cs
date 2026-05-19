using System.Globalization;
using System.Net;
using System.Text;
using SmartFuture.Shared.Enums.Communication;

namespace SmartFuture.Application.Communication.Email.Templates;

/// <summary>
/// Output of a template render. Carries everything the caller needs to
/// pass into <c>INotificationService.SendAsync</c>: a logical sender,
/// a subject, an HTML body, and a plain-text fallback for both
/// non-HTML clients and the OutboundNotifications audit row.
/// </summary>
public sealed record SmartFutureEmailContent(
    EmailSenderType SenderType,
    string Subject,
    string HtmlBody,
    string PlainTextBody);

/// <summary>
/// Shared HTML scaffolding for SmartFuture emails. Pure-C# string
/// builder with inline CSS so emails render in clients that strip
/// &lt;style&gt; blocks (Outlook, Gmail mobile). 640-pixel-wide single
/// column, navy header, light card body, footer.
///
/// **No external images**, no tracking pixels, no remote stylesheets.
/// Branding is a coloured header bar with the literal "Smart Future"
/// text to keep emails portable across CDN/firewall blocking.
/// </summary>
public static class SmartFutureEmailLayout
{
    private const string BrandNavy = "#0D1340";
    private const string BrandBlue = "#2630E8";
    private const string Slate = "#062851";
    private const string SlateMuted = "#062851"; // used with opacity classes inline
    private const string LightSurface = "#F6F8FD";
    private const string Border = "#D1DAE2";
    private const string Accent = "#9CF17B";

    private const string PreferredWidth = "640";

    /// <summary>
    /// Compose the final HTML for an email body. Pass the inner card
    /// content (already HTML); the layout wraps it in the standard
    /// header/footer.
    /// </summary>
    public static string Compose(string title, string preheader, string innerHtml)
    {
        var sb = new StringBuilder(8 * 1024);
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.Append("<title>").Append(Html(title)).AppendLine("</title>");
        sb.AppendLine("</head>");
        sb.Append("<body style=\"margin:0;padding:0;background:").Append(LightSurface)
          .Append(";font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Arial,sans-serif;color:")
          .Append(Slate).AppendLine(";\">");

        // Preheader — hidden preview text shown in inbox previews.
        sb.Append("<div style=\"display:none;font-size:1px;color:")
          .Append(LightSurface)
          .Append(";line-height:1px;max-height:0;max-width:0;opacity:0;overflow:hidden;\">")
          .Append(Html(preheader))
          .AppendLine("</div>");

        // Outer wrapper — full-width slate background so the card
        // floats nicely on desktop clients while collapsing on mobile.
        sb.Append("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"background:")
          .Append(LightSurface).AppendLine(";\">");
        sb.AppendLine("<tr><td align=\"center\" style=\"padding:24px 12px;\">");

        sb.Append("<table role=\"presentation\" width=\"").Append(PreferredWidth)
          .AppendLine("\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"width:100%;max-width:640px;\">");

        // Header band — solid navy with brand text + tagline. Done in
        // text rather than an image so emails render without external
        // requests.
        sb.AppendLine("<tr>");
        sb.Append("<td style=\"background:").Append(BrandNavy)
          .AppendLine(";border-radius:12px 12px 0 0;padding:24px 28px;\">");
        sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\">");
        sb.AppendLine("<tr>");
        sb.AppendLine("<td>");
        sb.AppendLine("<p style=\"margin:0;color:#ffffff;font-size:20px;font-weight:700;letter-spacing:-0.01em;\">Smart Future</p>");
        sb.Append("<p style=\"margin:4px 0 0;color:").Append(Accent)
          .AppendLine(";font-size:11px;font-weight:600;letter-spacing:0.12em;text-transform:uppercase;\">Connecting your future</p>");
        sb.AppendLine("</td>");
        sb.AppendLine("</tr>");
        sb.AppendLine("</table>");
        sb.AppendLine("</td>");
        sb.AppendLine("</tr>");

        // Card body — white surface, padding, rounded bottom.
        sb.AppendLine("<tr>");
        sb.Append("<td style=\"background:#ffffff;border:1px solid ").Append(Border)
          .AppendLine(";border-top:none;border-radius:0 0 12px 12px;padding:32px 28px;\">");
        sb.AppendLine(innerHtml);
        sb.AppendLine("</td>");
        sb.AppendLine("</tr>");

        // Footer — copyright, address-style line, support hint. No
        // unsubscribe link because these are transactional emails;
        // operational mailings would need one added.
        sb.AppendLine("<tr>");
        sb.Append("<td style=\"padding:20px 8px;color:")
          .Append(SlateMuted)
          .AppendLine(";opacity:0.55;font-size:12px;line-height:1.6;text-align:center;\">");
        sb.AppendLine("This is an automated message from Smart Future. Please do not reply unless invited to.<br>");
        sb.AppendLine("Need help? Contact <a href=\"mailto:support@smartfuture.co.za\" style=\"color:#2630E8;text-decoration:none;\">support@smartfuture.co.za</a>.<br>");
        sb.Append("&copy; ").Append(DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture))
          .AppendLine(" Smart Future. All rights reserved.");
        sb.AppendLine("</td>");
        sb.AppendLine("</tr>");

        sb.AppendLine("</table>");
        sb.AppendLine("</td></tr>");
        sb.AppendLine("</table>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    // ─── Reusable building blocks ────────────────────────────────────────

    public static string Heading(string text)
        => $"<h1 style=\"margin:0 0 16px;font-size:22px;line-height:1.3;font-weight:700;color:{Slate};\">{Html(text)}</h1>";

    public static string SubHeading(string text)
        => $"<h2 style=\"margin:24px 0 12px;font-size:15px;line-height:1.4;font-weight:700;color:{Slate};letter-spacing:0.02em;text-transform:uppercase;\">{Html(text)}</h2>";

    public static string Paragraph(string text)
        => $"<p style=\"margin:0 0 14px;font-size:14px;line-height:1.6;color:{Slate};\">{Html(text)}</p>";

    public static string ParagraphRaw(string html)
        => $"<p style=\"margin:0 0 14px;font-size:14px;line-height:1.6;color:{Slate};\">{html}</p>";

    public static string MutedNote(string text)
        => $"<p style=\"margin:0 0 14px;font-size:12px;line-height:1.5;color:{Slate};opacity:0.65;\">{Html(text)}</p>";

    public static string CallToActionButton(string href, string label)
    {
        var safeHref = WebUtility.HtmlEncode(href);
        return string.Concat(
            "<table role=\"presentation\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"margin:20px 0;\"><tr><td>",
            "<a href=\"", safeHref, "\" style=\"display:inline-block;background:", BrandBlue,
            ";color:#ffffff;text-decoration:none;font-weight:700;font-size:14px;padding:12px 22px;border-radius:10px;\">",
            Html(label), "</a></td></tr></table>");
    }

    /// <summary>
    /// Big, monospaced verification-code block — purpose-built for OTPs.
    /// Centered, easy to read, easy to copy.
    /// </summary>
    public static string CodeBlock(string code)
        => string.Concat(
            "<div style=\"margin:18px 0;padding:18px 12px;background:", LightSurface,
            ";border:1px solid ", Border, ";border-radius:12px;text-align:center;\">",
            "<p style=\"margin:0;font-family:'SFMono-Regular',Consolas,'Liberation Mono',Menlo,monospace;",
            "font-size:30px;letter-spacing:0.32em;font-weight:700;color:", BrandNavy, ";\">",
            Html(code), "</p>",
            "</div>");

    /// <summary>
    /// Compact key/value list rendered as a table for predictable
    /// alignment across email clients.
    /// </summary>
    public static string KeyValueTable(IReadOnlyList<(string Label, string Value)> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"margin:8px 0 18px;border-collapse:collapse;\">");
        for (var i = 0; i < rows.Count; i++)
        {
            var (label, value) = rows[i];
            if (string.IsNullOrWhiteSpace(value)) continue;
            var border = i == rows.Count - 1 ? string.Empty : $"border-bottom:1px solid {Border};";
            sb.Append("<tr><td style=\"padding:8px 0;font-size:12px;color:")
              .Append(Slate).Append(";opacity:0.55;width:40%;").Append(border).Append("\">")
              .Append(Html(label)).Append("</td>");
            sb.Append("<td style=\"padding:8px 0;font-size:14px;color:")
              .Append(Slate).Append(";font-weight:600;text-align:right;").Append(border).Append("\">")
              .Append(Html(value)).Append("</td></tr>");
        }
        sb.Append("</table>");
        return sb.ToString();
    }

    public static string Divider()
        => $"<hr style=\"border:0;border-top:1px solid {Border};margin:18px 0;\">";

    /// <summary>Safely HTML-encode a dynamic string. Always use this for user input.</summary>
    public static string Html(string? text)
        => WebUtility.HtmlEncode(text ?? string.Empty);
}

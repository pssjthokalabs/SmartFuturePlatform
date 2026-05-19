using System.Text;
using SmartFuture.Shared.Enums.Communication;

namespace SmartFuture.Application.Communication.Email.Templates;

/// <summary>
/// Support-ticket templates. Sent from
/// <see cref="EmailSenderType.Support"/> so replies to these emails
/// land in the right mailbox.
/// </summary>
public static class SupportEmailTemplates
{
    public class TicketCreatedModel
    {
        public string CustomerFirstName { get; set; } = string.Empty;
        public string TicketNumber { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string? Category { get; set; }
        public string? Priority { get; set; }
        public string? DescriptionPreview { get; set; }
        public string? TicketDetailUrl { get; set; }
    }

    public static SmartFutureEmailContent TicketCreated(TicketCreatedModel m)
    {
        var greeting = string.IsNullOrWhiteSpace(m.CustomerFirstName) ? "there" : m.CustomerFirstName;
        var subject = $"We've received your support request: {m.TicketNumber}";

        var rows = new List<(string Label, string Value)>
        {
            ("Ticket number", m.TicketNumber),
            ("Subject", m.Subject),
            ("Category", m.Category ?? string.Empty),
            ("Priority", m.Priority ?? string.Empty),
        };

        var inner = new StringBuilder();
        inner.Append(SmartFutureEmailLayout.Heading("We're on it"));
        inner.Append(SmartFutureEmailLayout.Paragraph($"Hi {greeting},"));
        inner.Append(SmartFutureEmailLayout.Paragraph(
            "Thanks for reaching out. We've opened a ticket and our support team will be in touch shortly. " +
            "Here are the details we have on file:"));
        inner.Append(SmartFutureEmailLayout.KeyValueTable(rows));

        if (!string.IsNullOrWhiteSpace(m.DescriptionPreview))
        {
            inner.Append(SmartFutureEmailLayout.SubHeading("Your message"));
            inner.Append(SmartFutureEmailLayout.Paragraph(m.DescriptionPreview!));
        }

        if (!string.IsNullOrWhiteSpace(m.TicketDetailUrl))
        {
            inner.Append(SmartFutureEmailLayout.CallToActionButton(m.TicketDetailUrl, "View ticket"));
        }

        inner.Append(SmartFutureEmailLayout.Divider());
        inner.Append(SmartFutureEmailLayout.MutedNote(
            "You can reply to this email or update the ticket from the Client Zone."));

        var html = SmartFutureEmailLayout.Compose(
            title: subject,
            preheader: $"Ticket {m.TicketNumber} created — {m.Subject}",
            innerHtml: inner.ToString());

        var plain = new StringBuilder()
            .AppendLine($"Hi {greeting},")
            .AppendLine()
            .AppendLine("Thanks for reaching out. We've opened a support ticket and our team will be in touch shortly.")
            .AppendLine()
            .AppendLine($"Ticket number: {m.TicketNumber}")
            .AppendLine($"Subject:       {m.Subject}")
            .AppendLine($"Category:      {m.Category ?? "—"}")
            .AppendLine($"Priority:      {m.Priority ?? "—"}")
            .AppendLine();

        if (!string.IsNullOrWhiteSpace(m.DescriptionPreview))
        {
            plain.AppendLine("Your message:").AppendLine(m.DescriptionPreview).AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(m.TicketDetailUrl))
        {
            plain.AppendLine($"View in Client Zone: {m.TicketDetailUrl}").AppendLine();
        }

        plain.AppendLine("— The Smart Future support team");

        return new SmartFutureEmailContent(EmailSenderType.Support, subject, html, plain.ToString());
    }

    public class TicketReplyModel
    {
        public string CustomerFirstName { get; set; } = string.Empty;
        public string TicketNumber { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string? Status { get; set; }
        public string? AgentName { get; set; }
        public string? TicketDetailUrl { get; set; }
    }

    public static SmartFutureEmailContent TicketReply(TicketReplyModel m)
    {
        var greeting = string.IsNullOrWhiteSpace(m.CustomerFirstName) ? "there" : m.CustomerFirstName;
        var subject = $"New reply on support ticket {m.TicketNumber}";

        var rows = new List<(string Label, string Value)>
        {
            ("Ticket number", m.TicketNumber),
            ("Subject", m.Subject),
            ("Status", m.Status ?? string.Empty),
            ("Agent", m.AgentName ?? string.Empty),
        };

        var inner = new StringBuilder();
        inner.Append(SmartFutureEmailLayout.Heading("New reply on your ticket"));
        inner.Append(SmartFutureEmailLayout.Paragraph($"Hi {greeting},"));
        inner.Append(SmartFutureEmailLayout.Paragraph(
            "An agent has replied to your support ticket. Please sign in to the Client Zone to view the full conversation:"));
        inner.Append(SmartFutureEmailLayout.KeyValueTable(rows));
        if (!string.IsNullOrWhiteSpace(m.TicketDetailUrl))
        {
            inner.Append(SmartFutureEmailLayout.CallToActionButton(m.TicketDetailUrl, "View reply"));
        }

        var html = SmartFutureEmailLayout.Compose(
            title: subject,
            preheader: $"Ticket {m.TicketNumber} has a new reply.",
            innerHtml: inner.ToString());

        var plain = new StringBuilder()
            .AppendLine($"Hi {greeting},")
            .AppendLine()
            .AppendLine("An agent has replied to your support ticket. Please sign in to view the full conversation.")
            .AppendLine()
            .AppendLine($"Ticket number: {m.TicketNumber}")
            .AppendLine($"Subject:       {m.Subject}")
            .AppendLine($"Status:        {m.Status ?? "—"}")
            .AppendLine();

        if (!string.IsNullOrWhiteSpace(m.TicketDetailUrl))
        {
            plain.AppendLine($"View ticket: {m.TicketDetailUrl}").AppendLine();
        }
        plain.AppendLine("— The Smart Future support team");

        return new SmartFutureEmailContent(EmailSenderType.Support, subject, html, plain.ToString());
    }

    public class TicketStatusChangedModel
    {
        public string CustomerFirstName { get; set; } = string.Empty;
        public string TicketNumber { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string NewStatus { get; set; } = string.Empty;
        public string? ResolutionSummary { get; set; }
        public string? TicketDetailUrl { get; set; }
    }

    public static SmartFutureEmailContent TicketStatusChanged(TicketStatusChangedModel m)
    {
        var greeting = string.IsNullOrWhiteSpace(m.CustomerFirstName) ? "there" : m.CustomerFirstName;
        var subject = $"Support ticket {m.NewStatus.ToLowerInvariant()}: {m.TicketNumber}";

        var rows = new List<(string Label, string Value)>
        {
            ("Ticket number", m.TicketNumber),
            ("Subject", m.Subject),
            ("New status", m.NewStatus),
        };

        var inner = new StringBuilder();
        inner.Append(SmartFutureEmailLayout.Heading($"Ticket {m.NewStatus.ToLowerInvariant()}"));
        inner.Append(SmartFutureEmailLayout.Paragraph($"Hi {greeting},"));
        inner.Append(SmartFutureEmailLayout.Paragraph(
            "Your support ticket status has changed. Here's the latest:"));
        inner.Append(SmartFutureEmailLayout.KeyValueTable(rows));

        if (!string.IsNullOrWhiteSpace(m.ResolutionSummary))
        {
            inner.Append(SmartFutureEmailLayout.SubHeading("Resolution"));
            inner.Append(SmartFutureEmailLayout.Paragraph(m.ResolutionSummary!));
        }

        if (!string.IsNullOrWhiteSpace(m.TicketDetailUrl))
        {
            inner.Append(SmartFutureEmailLayout.CallToActionButton(m.TicketDetailUrl, "View ticket"));
        }

        var html = SmartFutureEmailLayout.Compose(
            title: subject,
            preheader: $"Ticket {m.TicketNumber} is now {m.NewStatus}.",
            innerHtml: inner.ToString());

        var plain = new StringBuilder()
            .AppendLine($"Hi {greeting},")
            .AppendLine()
            .AppendLine("Your support ticket status has changed.")
            .AppendLine()
            .AppendLine($"Ticket number: {m.TicketNumber}")
            .AppendLine($"Subject:       {m.Subject}")
            .AppendLine($"New status:    {m.NewStatus}");

        if (!string.IsNullOrWhiteSpace(m.ResolutionSummary))
        {
            plain.AppendLine().AppendLine("Resolution:").AppendLine(m.ResolutionSummary);
        }
        plain.AppendLine();
        if (!string.IsNullOrWhiteSpace(m.TicketDetailUrl))
        {
            plain.AppendLine($"View ticket: {m.TicketDetailUrl}").AppendLine();
        }
        plain.AppendLine("— The Smart Future support team");

        return new SmartFutureEmailContent(EmailSenderType.Support, subject, html, plain.ToString());
    }
}

using SmartFuture.Shared.Enums.Communication;
using SmartFuture.Shared.Enums.Notifications;

namespace SmartFuture.Application.Notifications.Dtos;

public class SendNotificationRequestDto
{
    public Guid? UserId { get; set; }
    public NotificationChannel Channel { get; set; }
    public NotificationType Type { get; set; }

    public string? RecipientEmail { get; set; }
    public string? RecipientPhone { get; set; }

    public string? Subject { get; set; }
    public string Body { get; set; } = string.Empty;

    // ─── Multi-sender email fields (Phase 35) ────────────────────────────
    //
    // All optional, all backward-compatible. Older callers ignore them
    // and the multi-sender SMTP path treats them as: `Default` sender,
    // plain-text body. Templated callers populate them to deliver
    // branded HTML mail from the appropriate logical sender.

    /// <summary>
    /// Logical sender identity. Resolved at send time to a
    /// From-address + SMTP credentials by <c>EmailProviders:Senders</c>.
    /// Defaults to <see cref="EmailSenderType.Default"/> which the
    /// provider in turn resolves to <c>EmailProviders:DefaultSender</c>.
    /// Single-sender legacy <see cref="SmartFuture.Infrastructure.Notifications.SmtpEmailSender"/>
    /// and the logging sender ignore this field.
    /// </summary>
    public EmailSenderType SenderType { get; set; } = EmailSenderType.Default;

    /// <summary>
    /// When true the multi-sender SMTP path sends an HTML body
    /// (<see cref="HtmlBody"/>) with the plain-text <see cref="Body"/>
    /// as the alternative view. When false (default) the email is
    /// sent as plain text only — current behaviour.
    /// </summary>
    public bool IsHtml { get; set; }

    /// <summary>
    /// HTML body. Only consumed when <see cref="IsHtml"/> is true.
    /// The plain-text <see cref="Body"/> is still required and is
    /// what gets persisted to OutboundNotifications (HTML is too
    /// large/noisy for the audit table).
    /// </summary>
    public string? HtmlBody { get; set; }

    public string? RelatedEntityType { get; set; }
    public Guid? RelatedEntityId { get; set; }
    public string? MetadataJson { get; set; }
}

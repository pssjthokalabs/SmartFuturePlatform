using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Domain.Jobs;

// Append-only upload history for CV / cover-letter files. The CURRENT
// document is denormalised onto JobSubscriberProfile (single read for
// the common case); this table keeps every prior version so an admin can
// see when a CV was replaced and so a botched overwrite is recoverable.
//
// Objects live in a private R2 prefix — never linked directly. Reads go
// through the authenticated download endpoints, which resolve
// ObjectKey → stream.
public class JobSubscriberDocument : BaseEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public JobSubscriberDocumentType DocumentType { get; set; }

    public string ObjectKey { get; set; } = string.Empty;
    public string? FileUrl { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    // False once a newer upload of the same type supersedes this row.
    public bool IsCurrent { get; set; } = true;
    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
}

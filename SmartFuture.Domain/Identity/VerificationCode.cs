using SmartFuture.Domain.Common;
using SmartFuture.Shared.Enums.Identity;

namespace SmartFuture.Domain.Identity;

// One-time verification codes issued by the API for sensitive identity
// changes (password change, future phone/email change).
//
// `CodeHash` stores a SHA-256 hash of the issued code — the plaintext
// is delivered to the user out-of-band and never persisted. The
// confirm endpoint compares hashes and refuses to validate codes that
// are expired, already consumed, or have exceeded `MaxAttempts`.
public class VerificationCode : BaseEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public VerificationCodePurpose Purpose { get; set; }
    public VerificationCodeChannel Channel { get; set; }

    public string CodeHash { get; set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }

    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTime? LastAttemptAtUtc { get; set; }
}

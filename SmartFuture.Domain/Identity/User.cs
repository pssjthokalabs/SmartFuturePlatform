using Microsoft.AspNetCore.Identity;
using SmartFuture.Shared.Enums.Identity;

namespace SmartFuture.Domain.Identity;

public class User : IdentityUser<Guid>
{
    public User()
    {
        Id = Guid.NewGuid();
    }

    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public UserAccountStatus AccountStatus { get; set; } = UserAccountStatus.Active;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }

    // Phase 41 — short, human-friendly identifier surfaced in the admin
    // portal (rendered as "USR-1000" etc.). Allocated at user-create time
    // starting from 1000. Nullable so the EF migration can backfill
    // existing rows at startup before the unique index is enforced; once
    // backfilled, every row has a value.
    public int? UserNumber { get; set; }
}

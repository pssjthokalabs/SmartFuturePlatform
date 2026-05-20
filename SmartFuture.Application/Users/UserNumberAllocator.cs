using Microsoft.EntityFrameworkCore;
using SmartFuture.Application.Persistence;

namespace SmartFuture.Application.Users;

// Phase 41 — short, human-friendly user identifier rendered as
// "USR-1000" in the admin portal. The number is allocated at user-create
// time and persisted on the User row. A unique-filtered index on the DB
// catches accidental collisions; on a clash the caller should retry by
// asking for a fresh number (the value is recomputed from MAX+1 each
// time, so a single retry resolves typical races).
public static class UserNumberAllocator
{
    public const int MinUserNumber = 1000;

    public static async Task<int> AllocateNextAsync(IAppDbContext dbContext, CancellationToken cancellationToken = default)
    {
        var max = await dbContext.Users.MaxAsync(u => (int?)u.UserNumber, cancellationToken);
        return Math.Max(MinUserNumber, (max ?? (MinUserNumber - 1)) + 1);
    }
}

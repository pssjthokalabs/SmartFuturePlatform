using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Users;

// The ONE place that adds a product role to an existing user.
//
// Why it exists: SmartFuture now has two independent product lines that
// share a single Users table — the ISP customer journey and the Job
// Opportunities module. A person can legitimately be both. Every
// "become a customer" / "become a job subscriber" path must therefore be
// additive and idempotent, and must never remove or replace a role the
// user already holds.
//
// Keeping this behind one seam means the registration flows, the
// enrolment endpoints, and any future admin action all share the same
// audited implementation instead of each calling AddToRoleAsync by hand.
public interface IUserRoleUpgradeService
{
    // Adds the Customer role if missing and ensures a CustomerProfile
    // row exists. Idempotent: a user who is already a Customer is a
    // no-op success. NEVER touches other roles.
    Task<Result> EnsureCustomerRoleAsync(User user, string reason, CancellationToken cancellationToken = default);

    // Adds the JobSubscriber role if missing. Idempotent. NEVER touches
    // other roles — an existing Customer keeps Customer.
    Task<Result> EnsureJobSubscriberRoleAsync(User user, string reason, CancellationToken cancellationToken = default);
}

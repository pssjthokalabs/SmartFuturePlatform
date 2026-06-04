using SmartFuture.Domain.Billing;
using SmartFuture.Domain.Customers;
using SmartFuture.Domain.Identity;
using SmartFuture.Domain.Installations;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.Orders;
using SmartFuture.Domain.SupportTickets;

namespace SmartFuture.Application.Common;

/// <summary>
/// Reusable IQueryable filters that strip controlled QA test accounts
/// (User.IsTestAccount == true) and any record owned by one, so they never
/// pollute real business stats / dashboards / reports / operational queues.
///
/// Keyed on the persisted <c>User.IsTestAccount</c> flag — NOT the email
/// pattern — so the flag stays the single source of truth. Each overload
/// walks the entity's relationship back to the owning User:
///   CustomerProfile/Order/SupportTicket → User       (direct)
///   Invoice/Installation/NetworkAccount → Order.User
///   Payment                             → Invoice.Order.User
///
/// Default everywhere is EXCLUDE. Surfaces that intentionally show test data
/// (Users → Test tab, a test-account drill-down) simply don't call these.
/// EF translates the navigation walk to INNER JOINs on required FKs, so no
/// rows are dropped for real accounts.
/// </summary>
public static class TestAccountFilters
{
    public static IQueryable<User> ExcludeTestAccounts(this IQueryable<User> query)
        => query.Where(u => !u.IsTestAccount);

    public static IQueryable<CustomerProfile> ExcludeTestAccounts(this IQueryable<CustomerProfile> query)
        => query.Where(p => !p.User!.IsTestAccount);

    public static IQueryable<Order> ExcludeTestAccounts(this IQueryable<Order> query)
        => query.Where(o => !o.User!.IsTestAccount);

    public static IQueryable<Invoice> ExcludeTestAccounts(this IQueryable<Invoice> query)
        => query.Where(i => !i.Order!.User!.IsTestAccount);

    public static IQueryable<Installation> ExcludeTestAccounts(this IQueryable<Installation> query)
        => query.Where(x => !x.Order!.User!.IsTestAccount);

    public static IQueryable<NetworkAccount> ExcludeTestAccounts(this IQueryable<NetworkAccount> query)
        => query.Where(n => !n.Order!.User!.IsTestAccount);

    public static IQueryable<Payment> ExcludeTestAccounts(this IQueryable<Payment> query)
        => query.Where(p => !p.Invoice!.Order!.User!.IsTestAccount);

    public static IQueryable<SupportTicket> ExcludeTestAccounts(this IQueryable<SupportTicket> query)
        => query.Where(t => !t.User!.IsTestAccount);
}

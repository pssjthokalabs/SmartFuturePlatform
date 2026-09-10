using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.Orders;

namespace SmartFuture.Application.Openserve;

/// <summary>
/// Generates the Openserve "Subscriber Reference Number" for a
/// NetworkAccount. Isolated behind an interface specifically so the
/// convention can change the moment Openserve/the client specifies a
/// required format — callers never regenerate an existing value (see
/// OpenserveOrderSubmissionService), so a future format change only
/// affects NEW reservations, never in-flight ones.
/// </summary>
public interface IOpenserveSubscriberReferenceGenerator
{
    string Generate(NetworkAccount account, Order order);
}

/// <summary>
/// Default convention: "SF-{AccountNumber}". Piggybacks on
/// NetworkAccount.AccountNumber (already globally unique, already
/// generated once and stable) rather than inventing a second counter —
/// simplest thing that satisfies "unique, stable, easy to change later".
/// </summary>
public class DefaultOpenserveSubscriberReferenceGenerator : IOpenserveSubscriberReferenceGenerator
{
    public string Generate(NetworkAccount account, Order order) => $"SF-{account.AccountNumber}";
}

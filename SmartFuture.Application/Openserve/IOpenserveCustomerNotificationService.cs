using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Application.Openserve;

public interface IOpenserveCustomerNotificationService
{
    /// <summary>
    /// Decides whether an Openserve normalized-status transition is
    /// customer-meaningful and, if so, dispatches Email/SMS/WhatsApp via
    /// the existing INotificationService/OutboundNotification pipeline.
    /// Never throws. Returns true when a notification was actually
    /// dispatched (used by the caller to stamp
    /// OpenserveOrderStatusHistory.NotificationTriggered).
    /// </summary>
    Task<bool> NotifyStatusChangedAsync(
        OpenserveOrder openserveOrder, Order order,
        OpenserveProvisioningStatus previous, OpenserveProvisioningStatus current,
        CancellationToken cancellationToken = default);

    /// <summary>Fired once, separately, when TryOpenserveConfirmedActivateServiceAsync actually flips the order to Active (the true "service activated" moment — billing-anchored, not just Openserve's own "Accepted").</summary>
    Task<bool> NotifyServiceActivatedAsync(Order order, CancellationToken cancellationToken = default);
}

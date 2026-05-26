using SmartFuture.Shared.Enums.Orders;

namespace SmartFuture.Application.Orders.Dtos;

// Phase 51 — customer order-creation gate.
//
// Read by SmartFutureApp + customer-portal *before* the user starts the
// order wizard so the UI can pre-block with a friendly message instead
// of failing late inside the create call. The server also enforces the
// same rule inside `OrderService.CreateMineAsync` — this DTO exists to
// surface that decision early; it is *not* a substitute for the
// server-side guard.
//
// Business rule: SmartFuture customers are limited to one active /
// in-flight service at a time. Any Order in a non-terminal status
// (Draft / Submitted / Confirmed / AwaitingPayment / PaymentReceived /
// Provisioning / Active) blocks new order creation. Cancelled / Failed
// / Rejected orders DO NOT block.
public class CustomerOrderEligibilityDto
{
    public bool CanCreateOrder { get; set; }

    /// <summary>
    /// Stable, lower_snake_case discriminator the mobile/portal copy
    /// switches on. One of: "pending_order", "active_service", or null
    /// when <see cref="CanCreateOrder"/> is true.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>Display-ready single sentence shown to the customer.</summary>
    public string? Message { get; set; }

    public Guid?        BlockingOrderId          { get; set; }
    public string?      BlockingOrderNumber      { get; set; }
    public OrderStatus? BlockingOrderStatus      { get; set; }
    public string?      BlockingOrderPackageName { get; set; }
}

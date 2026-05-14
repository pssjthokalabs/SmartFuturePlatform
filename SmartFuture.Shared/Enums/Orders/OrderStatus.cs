namespace SmartFuture.Shared.Enums.Orders;

public enum OrderStatus
{
    Draft = 0,
    Submitted = 1,
    Confirmed = 2,
    AwaitingPayment = 3,
    PaymentReceived = 4,
    Provisioning = 5,
    Active = 6,
    Cancelled = 7,
    Failed = 8,
    Rejected = 9
}

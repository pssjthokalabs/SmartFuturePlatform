namespace SmartFuture.Shared.Enums.Payments;

public enum PaymentInitiationStatus
{
    Created = 0,
    RedirectRequired = 1,
    Pending = 2,
    Failed = 3,
    Cancelled = 4
}

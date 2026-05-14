namespace SmartFuture.Shared.Enums.Billing;

public enum InvoiceStatus
{
    Draft = 0,
    Issued = 1,
    PartiallyPaid = 2,
    Paid = 3,
    Overdue = 4,
    Void = 5,
    Cancelled = 6
}

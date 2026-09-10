namespace SmartFuture.Shared.Enums.Openserve;

// One value per Openserve API operation (Fulfilment API Spec §3-§8).
// Used to tag SmartFuture.Domain.Openserve.OpenserveIntegrationLog rows
// so support/admin can filter the raw request/response trail by what
// kind of call it was.
public enum OpenserveOperationType
{
    ProductQualification = 0,
    CreateOrder = 1,
    GetOrder = 2,
    CancelOrder = 3,
    PatchOrder = 4,
    GetActions = 5,
    ChangeOwnership = 6,
    Regrade = 7,
    ChangeProduct = 8,
    Suspend = 9,
    Resume = 10,
    RetrieveServiceDetails = 11,
    PostComment = 12,
    GetComment = 13,
    ProductInventory = 14,
    EventNotificationInbound = 15,
    CallbackInbound = 16
}

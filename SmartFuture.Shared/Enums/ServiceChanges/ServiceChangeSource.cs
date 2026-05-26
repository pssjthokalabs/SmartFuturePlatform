namespace SmartFuture.Shared.Enums.ServiceChanges;

// Origin of the change request — used for auditing + reports so we can
// tell self-service requests apart from admin-raised ones.
public enum ServiceChangeSource
{
    CustomerApp    = 0,
    CustomerPortal = 1,
    AdminManual    = 2
}

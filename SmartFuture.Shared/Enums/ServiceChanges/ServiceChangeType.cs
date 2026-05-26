namespace SmartFuture.Shared.Enums.ServiceChanges;

// Phase 51 — direction of a customer-initiated package switch.
// Determined server-side by comparing the requested package price
// against the active NetworkAccount's current price; the client may
// hint but the server is authoritative.
public enum ServiceChangeType
{
    Upgrade   = 0,
    Downgrade = 1
}

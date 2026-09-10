using SmartFuture.Domain.Common;
using SmartFuture.Domain.Orders;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Domain.Openserve;

// Tracks one SmartFuture Order's Openserve Product Order fulfilment
// lifecycle. One row per Order for its life (idempotent — retries of
// the same business submission update this row rather than creating a
// new one; ExternalReferenceNumber is generated once and never
// changes on retry, per brief §6).
//
// RawState is stored verbatim from Openserve (Appendix A: Pending,
// Validated, Acknowledged, In Progress, Assessing Cancellation,
// Pending Cancellation, Cancelled, Accepted — or any future value we
// don't recognise yet). NormalizedStatus is our own derived view for
// filtering/display; never trust it alone over RawState for support.
public class OpenserveOrder : BaseEntity
{
    public Guid OrderId { get; set; }
    public Order? Order { get; set; }

    /// <summary>
    /// The "External Reference Number" productCharacteristic — mandatory
    /// per the spec, must be unique per order, generated once at first
    /// submission and reused on every retry of that same submission.
    /// </summary>
    public string ExternalReferenceNumber { get; set; } = string.Empty;

    /// <summary>
    /// Most recent MessageID (UUID) header sent to Openserve. A NEW
    /// value is required per the spec on every individual HTTP
    /// invocation (even a retry of the same business operation) — this
    /// field is overwritten each attempt, unlike ExternalReferenceNumber.
    /// </summary>
    public string? LastMessageId { get; set; }

    /// <summary>correlationId as seen on the most recent event notification.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Openserve's own numeric order id ("id" in the ack/callback/event payload). Null until acknowledged.</summary>
    public string? OpenserveOrderId { get; set; }

    /// <summary>Openserve's "OrderName" (e.g. "SO303654"), seen on event notifications.</summary>
    public string? OpenserveOrderName { get; set; }

    /// <summary>
    /// Denormalized copy of the subscriber reference used on this
    /// submission. Source of truth is
    /// NetworkAccount.OpenserveSubscriberReferenceNumber — copied here
    /// purely so this row is self-contained for support/troubleshooting.
    /// </summary>
    public string? SubscriberReferenceNumber { get; set; }

    /// <summary>Raw "@type" sent — "Sales Order" | "Alter Product Options" | "Cancel Market Offer". Not an enum: Openserve may add types.</summary>
    public string OrderType { get; set; } = string.Empty;

    /// <summary>Raw "reason" sent, when applicable — e.g. "Regrade", "Product Conversion", "Receive Ownership".</summary>
    public string? Reason { get; set; }

    /// <summary>Verbatim Openserve order state string. Never overwritten with a guess — an unrecognised value is stored as-is.</summary>
    public string? RawState { get; set; }

    public OpenserveProvisioningStatus NormalizedStatus { get; set; } = OpenserveProvisioningStatus.NotSubmitted;

    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? LastOpenserveUpdateAtUtc { get; set; }
    public DateTime? LastSuccessfulSyncAtUtc { get; set; }

    public int RetryCount { get; set; }
    public string? LastFailureCode { get; set; }
    public string? LastFailureMessage { get; set; }

    public Guid? PackageOpenserveMappingId { get; set; }
    public PackageOpenserveMapping? PackageOpenserveMapping { get; set; }

    /// <summary>True once RawState is Accepted/Cancelled — the reconciliation worker stops polling a terminal order.</summary>
    public bool IsTerminal { get; set; }
}

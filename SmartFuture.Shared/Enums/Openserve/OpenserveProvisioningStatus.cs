namespace SmartFuture.Shared.Enums.Openserve;

// Our own normalized view of an Openserve Product Order's lifecycle,
// derived from the raw Appendix A state string (Order.OpenserveOrder
// keeps the raw string too — never rely on this enum alone for
// something support needs to debug).
//
// NotSubmitted/Submitting/Failed are OUR bookkeeping around the HTTP
// call itself and have no Openserve equivalent. Submitted..Completed
// map onto Openserve's own Pending/Validated/Acknowledged/InProgress/
// AssessingCancellation/PendingCancellation/Cancelled/Accepted set.
// Unknown is deliberate: an unrecognised future Openserve state must
// be stored, not rejected (brief §8).
public enum OpenserveProvisioningStatus
{
    NotSubmitted = 0,
    Submitting = 1,
    Submitted = 2,
    InProgress = 3,
    AwaitingCancellation = 4,
    Cancelled = 5,
    Completed = 6,
    Failed = 7,
    Unknown = 8
}

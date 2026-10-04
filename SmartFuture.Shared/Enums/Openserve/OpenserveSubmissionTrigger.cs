namespace SmartFuture.Shared.Enums.Openserve;

// Which path asked for an Openserve Create Order attempt. Every path
// runs through the same coordinator
// (OpenserveOrderSubmissionService.SubmitAsync); this only records who
// started it, for the order's activity history and the rules each path
// is allowed to apply.
public enum OpenserveSubmissionTrigger
{
    /// <summary>The business trigger — NetworkAccountService reserved the order's network account (payment landed).</summary>
    AutomaticInitial = 0,

    /// <summary>An Admin clicked Send to Openserve / Retry Openserve Submission.</summary>
    AdminManual = 1,

    /// <summary>The submission-recovery worker resending a Retryable failure.</summary>
    BackgroundRetry = 2,

    /// <summary>The safety sweep found an eligible Fibre order that never got a submission record.</summary>
    SafetySweep = 3
}

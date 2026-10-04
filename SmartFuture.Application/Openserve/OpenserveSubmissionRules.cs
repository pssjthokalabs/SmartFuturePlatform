using Microsoft.EntityFrameworkCore;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.Orders;

namespace SmartFuture.Application.Openserve;

/// <summary>Transport-level error codes OpenserveApiClient puts on a failed result (HTTP-level failures carry Openserve's own code or the status name).</summary>
public static class OpenserveApiErrorCodes
{
    public const string Timeout = "TIMEOUT";
    public const string TransportError = "TRANSPORT_ERROR";
    public const string ConnectionFailed = "CONNECTION_FAILED";
    public const string ParseError = "PARSE_ERROR";

    /// <summary>Set by SmartFuture (not the client) when a claim went stale — the attempt was cut off before its result was saved.</summary>
    public const string Interrupted = "INTERRUPTED";
}

/// <summary>LastFailureCode values for a submission that was never sent because a SmartFuture-side precondition failed.</summary>
public static class OpenserveBlockedCodes
{
    public const string Configuration = "BLOCKED_CONFIGURATION";
    public const string Mapping = "BLOCKED_MAPPING";
    public const string Amid = "BLOCKED_AMID";

    /// <summary>AMID present, but Openserve returned several building/unit rows and none is chosen yet.</summary>
    public const string BuildingUnit = "BLOCKED_BUILDING_UNIT";
    public const string Address = "BLOCKED_ADDRESS";
    public const string Contact = "BLOCKED_CONTACT";
}

/// <summary>
/// Decides whether a failed Create Order may be sent again. Driven only by
/// what OpenserveApiClient actually reports (HTTP status + error code).
/// The rule that matters: a request that MAY have reached Openserve is
/// never resent automatically, because Openserve offers no idempotency key
/// and no way to look an order up by our External Reference Number.
/// </summary>
public static class OpenserveSubmissionFailureClassifier
{
    public static (OpenserveSubmissionFailureClass Class, string Explanation) Classify(int? httpStatusCode, string? errorCode)
    {
        switch (errorCode)
        {
            case OpenserveApiErrorCodes.ConnectionFailed:
                return (OpenserveSubmissionFailureClass.Retryable, "SmartFuture could not connect to Openserve, so the order was not sent. It will be retried automatically.");
            case OpenserveApiErrorCodes.Timeout:
                return (OpenserveSubmissionFailureClass.OutcomeUnknown, "Openserve did not answer in time. It may still have received and created the order, so it is not resent automatically.");
            case OpenserveApiErrorCodes.TransportError:
                return (OpenserveSubmissionFailureClass.OutcomeUnknown, "The connection to Openserve dropped during the request. Openserve may have received the order, so it is not resent automatically.");
            case OpenserveApiErrorCodes.Interrupted:
                return (OpenserveSubmissionFailureClass.OutcomeUnknown, "The submission was interrupted before SmartFuture saved Openserve's answer (e.g. the API restarted). Openserve may have received the order.");
        }

        if (httpStatusCode is >= 200 and < 300)
        {
            return errorCode == OpenserveApiErrorCodes.ParseError
                ? (OpenserveSubmissionFailureClass.OutcomeUnknown, "Openserve answered with a success status but the response could not be read, so it may have accepted the order.")
                : (OpenserveSubmissionFailureClass.NonRetryable, "Openserve rejected the order. Correct the cause, then retry.");
        }

        return httpStatusCode switch
        {
            408 or 429 or 503 => (OpenserveSubmissionFailureClass.Retryable, $"Openserve was temporarily unavailable (HTTP {httpStatusCode}) and did not process the order. It will be retried automatically."),
            401 or 403 => (OpenserveSubmissionFailureClass.NonRetryable, $"Openserve refused SmartFuture's credentials (HTTP {httpStatusCode}). Check the API key and ISP settings, then retry."),
            >= 400 and < 500 => (OpenserveSubmissionFailureClass.NonRetryable, $"Openserve rejected the order (HTTP {httpStatusCode}). Correct the cause, then retry."),
            >= 500 => (OpenserveSubmissionFailureClass.OutcomeUnknown, $"Openserve returned a server/gateway error (HTTP {httpStatusCode}). It may have processed the order, so it is not resent automatically."),
            _ => (OpenserveSubmissionFailureClass.OutcomeUnknown, "The result of the request is unknown, so it is not resent automatically.")
        };
    }

    /// <summary>Plain-language note for Admin on why (or whether) SmartFuture will retry this class by itself.</summary>
    public static string ExplainClass(OpenserveSubmissionFailureClass failureClass) => failureClass switch
    {
        OpenserveSubmissionFailureClass.Retryable => "Openserve did not process the request, so it is safe to send again.",
        OpenserveSubmissionFailureClass.NonRetryable => "Openserve rejected the request. Sending it again unchanged would fail the same way.",
        OpenserveSubmissionFailureClass.Blocked => "The order was never sent. A SmartFuture-side requirement is missing.",
        OpenserveSubmissionFailureClass.OutcomeUnknown => "Openserve may have received the order. Resending could create a duplicate Openserve order.",
        _ => "No failure recorded."
    };
}

/// <summary>
/// The rules every submission path shares — the coordinator enforces them,
/// the recovery worker selects with them, and the Admin Order Detail
/// reports them, so the UI can never offer an action the backend refuses.
/// </summary>
public static class OpenserveSubmissionRules
{
    public const string SalesOrderType = "Sales Order";

    /// <summary>Payment has landed and the order is not cancelled/rejected/failed — the only states in which any Openserve submission may happen.</summary>
    public static readonly OrderStatus[] SubmittableOrderStatuses =
        { OrderStatus.PaymentReceived, OrderStatus.Provisioning, OrderStatus.PendingPayment, OrderStatus.PendingActivation, OrderStatus.Active };

    /// <summary>
    /// What the safety sweep looks for: paid, installation not yet done. An order
    /// already Active/PendingPayment/PendingActivation without a submission record
    /// was fulfilled some other way (e.g. ordered on the Openserve portal) and must
    /// never be sent automatically.
    /// </summary>
    public static readonly OrderStatus[] SweepOrderStatuses = { OrderStatus.PaymentReceived, OrderStatus.Provisioning };

    private static readonly OpenserveProvisioningStatus[] ForwardedStatuses =
    {
        OpenserveProvisioningStatus.Submitted, OpenserveProvisioningStatus.InProgress, OpenserveProvisioningStatus.AwaitingCancellation,
        OpenserveProvisioningStatus.Cancelled, OpenserveProvisioningStatus.Completed, OpenserveProvisioningStatus.Unknown
    };

    /// <summary>Openserve accepted this order at some point. From here only reconciliation (GET) applies — Create Order is never sent again.</summary>
    public static bool IsForwarded(OpenserveOrder o) => o.SubmittedAtUtc is not null || !string.IsNullOrWhiteSpace(o.OpenserveOrderId) || ForwardedStatuses.Contains(o.NormalizedStatus);

    public static bool IsStaleSubmitting(OpenserveOrder o, DateTime nowUtc, int staleMinutes)
    {
        if (o.NormalizedStatus != OpenserveProvisioningStatus.Submitting) return false;
        var claimedAt = o.LastSubmissionAttemptAtUtc ?? o.UpdatedAtUtc ?? o.CreatedAtUtc;
        return claimedAt <= nowUtc.AddMinutes(-Math.Max(1, staleMinutes));
    }

    /// <summary>Order-level reason no Openserve submission may happen right now (cancelled, unpaid, paused), or null.</summary>
    public static string? OrderGateReason(Order order)
    {
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Failed)
            return $"The SmartFuture order is {order.Status} — it must not be sent to Openserve.";
        if (!SubmittableOrderStatuses.Contains(order.Status))
            return $"The SmartFuture order is {order.Status} — it is sent to Openserve once payment has been received.";
        if (order.OpenserveAutomationPaused)
            return "Openserve automation is paused for this order by an Admin. Resume it to allow submission.";
        return null;
    }

    public static IReadOnlyList<string> MissingConfiguration(OpenserveFulfilmentSettings settings)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.BaseUrl)) missing.Add("Base URL");
        if (string.IsNullOrWhiteSpace(settings.ApiKey)) missing.Add("API key");
        if (string.IsNullOrWhiteSpace(settings.WsIspCode)) missing.Add("ws-ispcode");
        if (string.IsNullOrWhiteSpace(settings.IspIdentifier)) missing.Add("ISP Identifier");
        if (string.IsNullOrWhiteSpace(settings.SenderId)) missing.Add("Sender ID");
        if (string.IsNullOrWhiteSpace(settings.ReplyToAddress)) missing.Add("ReplyToAddress");
        return missing;
    }

    /// <summary>
    /// Everything that must be true before a real HTTP request goes to Openserve.
    /// Returns a blocker code + human-readable reason, or null when clear to send.
    /// Re-checked on every attempt, so a retry after the Admin fixes something
    /// uses the corrected data rather than resending stale values.
    /// </summary>
    public static (string Code, string Reason)? PreflightBlocker(Order order, PackageOpenserveMapping? mapping, OpenserveFulfilmentSettings settings, bool? coordinatesAvailable = null)
    {
        var missing = MissingConfiguration(settings);
        if (missing.Count > 0)
            return (OpenserveBlockedCodes.Configuration, $"Openserve configuration is incomplete (missing: {string.Join(", ", missing)}). Fix it under Admin > Integrations > Openserve > Configuration, then retry.");

        if (mapping is null)
            return (OpenserveBlockedCodes.Mapping, $"No enabled Openserve package mapping exists for package '{order.PackageName}'. Configure and enable one under Admin > Integrations > Openserve > Package Mappings, then retry.");

        if (!OpenserveProductCatalogue.IsOrderableAsNewSalesOrder(mapping.Sku, mapping.Capacity, mapping.CapacityUom))
            return (OpenserveBlockedCodes.Mapping, $"The Openserve mapping for package '{order.PackageName}' ({mapping.Sku} {mapping.Capacity} {mapping.CapacityUom}) is not orderable as a new Sales Order (retention offer or undocumented speed). Correct the mapping, then retry.");

        if (string.IsNullOrWhiteSpace(order.OpenserveAmId))
            return (OpenserveBlockedCodes.Amid, AmidMissingReason(order, coordinatesAvailable));

        // Separate from the AMID: a multi-unit address must name the exact
        // building/unit (BLD_NUM_ID) Openserve returned — never guessed.
        if (OpenserveBuildingCandidates.NeedsResolution(order))
            return (OpenserveBlockedCodes.BuildingUnit, OpenserveBuildingCandidates.MultipleUnitsReason);

        if (string.IsNullOrWhiteSpace(order.AddressLine1))
            return (OpenserveBlockedCodes.Address, "Order is missing a street address.");

        if (string.IsNullOrWhiteSpace(order.FullName))
            return (OpenserveBlockedCodes.Contact, "Order is missing the customer's full name.");

        if (string.IsNullOrWhiteSpace(order.PhoneNumber) && string.IsNullOrWhiteSpace(order.Email))
            return (OpenserveBlockedCodes.Contact, "Order is missing both a contact phone number and email.");

        return null;
    }

    /// <summary>
    /// Why there is no AMID, from what the order records: qualification never
    /// ran, ran and failed (with Openserve's reason), or can't run because the
    /// installation coordinates are missing. Never a generic guess.
    /// </summary>
    public static string AmidMissingReason(Order order, bool? coordinatesAvailable = null)
    {
        const string prefix = "Order has no Openserve AMID (Address Master Identifier).";
        var recorded = order.OpenserveQualificationFailureReason;

        if (recorded == OpenserveQualificationService.MissingCoordinatesReason || (order.OpenserveQualifiedAtUtc is null && coordinatesAvailable == false))
            return $"{prefix} {OpenserveQualificationService.MissingCoordinatesReason}";
        if (order.OpenserveQualifiedAtUtc is { } ranAt)
            return $"{prefix} Product Qualification ran at {ranAt:yyyy-MM-dd HH:mm} UTC and did not return one: {recorded ?? "no AMID returned"}";
        return $"{prefix} Product Qualification has not run for this address yet — use Run Product Qualification on the order.";
    }

    /// <summary>Blocked rows written before blocker codes were recorded only have the message — recover the category from it.</summary>
    public static string InferBlockedCode(string? lastFailureCode, string? lastFailureMessage)
    {
        if (!string.IsNullOrWhiteSpace(lastFailureCode) && lastFailureCode.StartsWith("BLOCKED_", StringComparison.Ordinal)) return lastFailureCode;
        var message = lastFailureMessage ?? string.Empty;
        if (message.Contains("multiple units", StringComparison.OrdinalIgnoreCase)) return OpenserveBlockedCodes.BuildingUnit;
        if (message.Contains("mapping", StringComparison.OrdinalIgnoreCase)) return OpenserveBlockedCodes.Mapping;
        if (message.Contains("configuration", StringComparison.OrdinalIgnoreCase)) return OpenserveBlockedCodes.Configuration;
        if (message.Contains("AMID", StringComparison.Ordinal)) return OpenserveBlockedCodes.Amid;
        if (message.Contains("address", StringComparison.OrdinalIgnoreCase)) return OpenserveBlockedCodes.Address;
        return OpenserveBlockedCodes.Contact;
    }

    /// <summary>Delay before automatic resend number <paramref name="automaticRetryNumber"/> (1-based): base × 2^(n−1), capped.</summary>
    public static TimeSpan BackoffDelay(int automaticRetryNumber, OpenserveSubmissionRecoverySettings settings)
    {
        var baseMinutes = Math.Max(1, settings.BaseRetryDelayMinutes);
        var maxMinutes = Math.Max(baseMinutes, settings.MaxRetryDelayMinutes);
        var exponent = Math.Clamp(automaticRetryNumber - 1, 0, 20);
        var minutes = Math.Min(maxMinutes, baseMinutes * Math.Pow(2, exponent));
        return TimeSpan.FromMinutes(minutes);
    }

    public static NetworkAccountStatus[] LiveNetworkAccountStatuses { get; } =
        { NetworkAccountStatus.Pending, NetworkAccountStatus.Active, NetworkAccountStatus.Suspended, NetworkAccountStatus.Failed };

    /// <summary>The network account a submission uses — the order's non-terminated one.</summary>
    public static NetworkAccount? PickNetworkAccount(IEnumerable<NetworkAccount> accounts) =>
        accounts.Where(a => LiveNetworkAccountStatuses.Contains(a.Status)).OrderByDescending(a => a.CreatedAtUtc).FirstOrDefault();
}

/// <summary>
/// The claim every resend takes before anything is sent: one conditional
/// UPDATE Failed/NotSubmitted → Submitting, matched against the exact snapshot
/// that passed validation (status, failure class and last-attempt time act as
/// the version) and never on a record Openserve already accepted. The database
/// applies it atomically, so of any number of concurrent callers — Admin
/// double-click, background retry, another API instance — exactly one wins.
/// </summary>
public static class OpenserveSubmissionClaim
{
    public static async Task<bool> TryClaimAsync(IAppDbContext dbContext, OpenserveOrder validated, OpenserveSubmissionTrigger trigger, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (validated.NormalizedStatus is not (OpenserveProvisioningStatus.Failed or OpenserveProvisioningStatus.NotSubmitted)) return false;

        var expectedStatus = validated.NormalizedStatus;
        var expectedClass = validated.LastFailureClass;
        var expectedAttemptAt = validated.LastSubmissionAttemptAtUtc;
        var rows = await dbContext.OpenserveOrders
            .Where(o => o.Id == validated.Id && o.NormalizedStatus == expectedStatus && o.LastFailureClass == expectedClass && o.LastSubmissionAttemptAtUtc == expectedAttemptAt
                && o.OpenserveOrderId == null && o.SubmittedAtUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.NormalizedStatus, OpenserveProvisioningStatus.Submitting)
                .SetProperty(o => o.LastSubmissionAttemptAtUtc, nowUtc)
                .SetProperty(o => o.LastSubmissionTrigger, trigger)
                .SetProperty(o => o.UpdatedAtUtc, nowUtc), cancellationToken);
        return rows == 1;
    }
}

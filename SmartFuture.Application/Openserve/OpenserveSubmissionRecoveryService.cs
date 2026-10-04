using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Application.Openserve;

public interface IOpenserveSubmissionRecoveryService
{
    /// <summary>Records interrupted claims as outcome-unknown, then resends Retryable failures whose backoff has elapsed.</summary>
    Task<OpenserveRecoveryPassResult> RunRetryPassAsync(CancellationToken cancellationToken = default);

    /// <summary>Finds Fibre orders that should have been submitted but have no submission record at all, and submits them.</summary>
    Task<OpenserveRecoveryPassResult> RunSafetySweepAsync(CancellationToken cancellationToken = default);
}

public sealed record OpenserveRecoveryPassResult(int Candidates, int Submitted, int Failed, int Blocked, int Refused, int InterruptedResolved = 0)
{
    public static readonly OpenserveRecoveryPassResult Skipped = new(0, 0, 0, 0, 0);
    public bool HadWork => Candidates > 0 || InterruptedResolved > 0;
}

/// <summary>
/// SUBMISSION RECOVERY — "SmartFuture failed to create/send the order".
/// Not reconciliation (polling Openserve for an order it already accepted):
/// nothing here looks at, or resends, an order Openserve has. Every send
/// goes through <see cref="IOpenserveOrderSubmissionService.SubmitAsync"/>,
/// which re-checks eligibility and claims the record, so the worker, the
/// sweep, the automatic trigger and Admin can never double-send.
/// </summary>
public class OpenserveSubmissionRecoveryService : IOpenserveSubmissionRecoveryService
{
    private readonly IAppDbContext _dbContext;
    private readonly IOpenserveOrderSubmissionService _submission;
    private readonly IOpenserveRuntimeConfigProvider _configProvider;
    private readonly ILogger<OpenserveSubmissionRecoveryService> _logger;

    public OpenserveSubmissionRecoveryService(IAppDbContext dbContext, IOpenserveOrderSubmissionService submission, IOpenserveRuntimeConfigProvider configProvider, ILogger<OpenserveSubmissionRecoveryService> logger)
    {
        _dbContext = dbContext;
        _submission = submission;
        _configProvider = configProvider;
        _logger = logger;
    }

    public async Task<OpenserveRecoveryPassResult> RunRetryPassAsync(CancellationToken cancellationToken = default)
    {
        var settings = _configProvider.Current;
        if (!settings.Enabled || !settings.SubmissionRecovery.Enabled) return OpenserveRecoveryPassResult.Skipped;

        var interrupted = await _submission.ResolveStaleSubmissionsAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var dueOrderIds = await _dbContext.OpenserveOrders.AsNoTracking()
            .Where(o => o.NormalizedStatus == OpenserveProvisioningStatus.Failed
                        && o.LastFailureClass == OpenserveSubmissionFailureClass.Retryable
                        && o.NextAutomaticRetryAtUtc != null && o.NextAutomaticRetryAtUtc <= now
                        && o.OpenserveOrderId == null && o.SubmittedAtUtc == null
                        && o.OrderType == OpenserveSubmissionRules.SalesOrderType
                        && o.Order != null && !o.Order.OpenserveAutomationPaused
                        && OpenserveSubmissionRules.SubmittableOrderStatuses.Contains(o.Order.Status))
            .OrderBy(o => o.NextAutomaticRetryAtUtc)
            .Select(o => o.OrderId)
            .Take(Math.Max(1, settings.SubmissionRecovery.MaxOrdersPerRun))
            .ToListAsync(cancellationToken);

        var result = await SubmitEachAsync(dueOrderIds, OpenserveSubmissionTrigger.BackgroundRetry, cancellationToken);
        return result with { InterruptedResolved = interrupted };
    }

    public async Task<OpenserveRecoveryPassResult> RunSafetySweepAsync(CancellationToken cancellationToken = default)
    {
        var settings = _configProvider.Current;
        if (!settings.Enabled || !settings.SubmissionRecovery.Enabled) return OpenserveRecoveryPassResult.Skipped;

        var (floor, ceiling) = await GetSafetySweepWindowAsync(_dbContext, settings.SubmissionRecovery, DateTime.UtcNow, cancellationToken);
        var candidateOrderIds = await SweepCandidates(_dbContext, floor, ceiling)
            .OrderBy(o => o.CreatedAtUtc)
            .Select(o => o.Id)
            .Take(Math.Max(1, settings.SubmissionRecovery.MaxOrdersPerRun))
            .ToListAsync(cancellationToken);

        if (candidateOrderIds.Count > 0)
            _logger.LogWarning("[Openserve][sweep] {Count} eligible Fibre order(s) had no Openserve submission record — submitting them now.", candidateOrderIds.Count);

        return await SubmitEachAsync(candidateOrderIds, OpenserveSubmissionTrigger.SafetySweep, cancellationToken);
    }

    /// <summary>
    /// The sweep's business-state query: paid Fibre orders whose installation
    /// hasn't happened (network account still Pending, reserved inside the
    /// window), not paused, with NO submission record of any kind. Orders
    /// that already have a record are the retry pass's / Admin's business.
    /// </summary>
    public static IQueryable<Domain.Orders.Order> SweepCandidates(IAppDbContext dbContext, DateTime floor, DateTime ceiling) =>
        dbContext.Orders.AsNoTracking()
            .Where(o => o.PackageType == ServicePackageType.Fibre
                        && OpenserveSubmissionRules.SweepOrderStatuses.Contains(o.Status)
                        && !o.OpenserveAutomationPaused
                        && !dbContext.OpenserveOrders.Any(x => x.OrderId == o.Id)
                        && dbContext.NetworkAccounts.Any(n => n.OrderId == o.Id && n.Status == NetworkAccountStatus.Pending && n.CreatedAtUtc >= floor && n.CreatedAtUtc <= ceiling));

    /// <summary>
    /// [floor, ceiling] on the network account's reservation time. Floor = the
    /// latest of: lookback window, the last time an Admin switched the
    /// integration on (orders paid while it was off may have been ordered on
    /// the Openserve portal by hand — never auto-send those), and the optional
    /// configured NotBefore. Ceiling = now − grace, so the sweep never races the
    /// automatic trigger for a brand-new order.
    /// </summary>
    public static async Task<(DateTime Floor, DateTime Ceiling)> GetSafetySweepWindowAsync(IAppDbContext dbContext, OpenserveSubmissionRecoverySettings recovery, DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var floor = nowUtc.AddDays(-Math.Max(1, recovery.SafetySweepLookbackDays));

        var lastEnabledAt = await dbContext.AuditLogs.AsNoTracking()
            .Where(a => a.ActionType == AuditActionType.OpenserveIntegrationEnabled)
            .OrderByDescending(a => a.CreatedAtUtc)
            .Select(a => (DateTime?)a.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (lastEnabledAt > floor) floor = lastEnabledAt.Value;
        if (recovery.SafetySweepNotBeforeUtc > floor) floor = recovery.SafetySweepNotBeforeUtc.Value;

        var ceiling = nowUtc.AddMinutes(-Math.Max(0, recovery.SafetySweepGraceMinutes));
        return (floor, ceiling);
    }

    private async Task<OpenserveRecoveryPassResult> SubmitEachAsync(IReadOnlyList<Guid> orderIds, OpenserveSubmissionTrigger trigger, CancellationToken cancellationToken)
    {
        int submitted = 0, failed = 0, blocked = 0, refused = 0;
        foreach (var orderId in orderIds)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                var attempt = await _submission.SubmitAsync(new OpenserveSubmissionRequest(orderId, trigger), cancellationToken);
                if (!attempt.IsSuccess)
                {
                    refused++;
                    _logger.LogInformation("[Openserve][recovery] {Trigger} for order {OrderId} not attempted: {Message}", trigger, orderId, attempt.Message);
                    continue;
                }

                switch (attempt.Data!.Outcome)
                {
                    case OpenserveSubmissionOutcome.Submitted: submitted++; break;
                    case OpenserveSubmissionOutcome.Blocked: blocked++; break;
                    default: failed++; break;
                }
            }
            catch (Exception ex)
            {
                // One order's problem must never stop the rest.
                refused++;
                _logger.LogError(ex, "[Openserve][recovery] {Trigger} threw for order {OrderId}.", trigger, orderId);
            }
        }
        return new OpenserveRecoveryPassResult(orderIds.Count, submitted, failed, blocked, refused);
    }
}

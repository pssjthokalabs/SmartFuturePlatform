using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Recurring;
using SmartFuture.Application.Payments.Recurring.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.API.Controllers;

/// <summary>
/// UAT-ONLY recurring-billing test harness. Lets an admin trigger a real
/// engine run and nudge dates from Swagger WITHOUT editing SQL — while always
/// using the real engine path.
///
/// HARD SAFETY:
///   • Production is refused (403) regardless of any flag.
///   • Disabled by default (<c>RecurringBillingTestHarness__Enabled=false</c>).
///   • RequireAdmin + per-action flag + confirmation phrase on every call.
///   • <c>run-once</c> with <c>dryRun=false</c> also needs <c>AllowRealChargeRun=true</c>.
///   • NO direct charge / mark-paid / provider call / settlement. <c>run-once</c>
///     calls the same <see cref="IRecurringBillingOrchestrator"/> as the hosted
///     service (run-lock + BillingRunLog reused); nudges move a single date
///     field so the real stages act on the next run.
///   • No token / authorization code / card / raw payload / passphrase /
///     signature is ever read, returned, or logged.
/// </summary>
[Route("api/admin/recurring-billing/test")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class AdminRecurringBillingTestController : BaseController
{
    private readonly IRecurringBillingOrchestrator _orchestrator;
    private readonly IAppDbContext _dbContext;
    private readonly IHostEnvironment _env;
    private readonly ICurrentUserService _currentUser;
    private readonly AutoBillingSettings _autoSettings;
    private readonly RecurringBillingTestHarnessSettings _harness;
    private readonly ILogger<AdminRecurringBillingTestController> _logger;

    public AdminRecurringBillingTestController(
        IRecurringBillingOrchestrator orchestrator,
        IAppDbContext dbContext,
        IHostEnvironment env,
        ICurrentUserService currentUser,
        IOptions<AutoBillingSettings> autoSettings,
        IOptions<RecurringBillingTestHarnessSettings> harness,
        ILogger<AdminRecurringBillingTestController> logger)
    {
        _orchestrator = orchestrator;
        _dbContext = dbContext;
        _env = env;
        _currentUser = currentUser;
        _autoSettings = autoSettings.Value;
        _harness = harness.Value;
        _logger = logger;
    }

    // ─── Shared gate ────────────────────────────────────────────────
    // Returns a non-null IActionResult when the request must be refused.

    private IActionResult? Guard(string action, bool actionFlag, string? confirmationPhrase)
    {
        if (_env.IsProduction())
            return Blocked(action, "production", StatusCodes.Status403Forbidden);

        if (!_harness.Enabled)
            return Blocked(action, "harness_disabled", StatusCodes.Status503ServiceUnavailable);

        if (!actionFlag)
            return Blocked(action, "action_disabled", StatusCodes.Status503ServiceUnavailable);

        if (string.IsNullOrEmpty(confirmationPhrase)
            || !string.Equals(confirmationPhrase, _harness.ConfirmationPhrase, StringComparison.Ordinal))
            return Blocked(action, "confirmation_mismatch", StatusCodes.Status400BadRequest);

        return null;
    }

    private IActionResult Blocked(string action, string reason, int statusCode)
    {
        _logger.LogWarning(
            "[recurring-billing][test-harness][blocked] action={Action} reason={Reason} actor={Actor} env={Env}",
            action, reason, _currentUser.UserId, _env.EnvironmentName);

        return StatusCode(statusCode, new
        {
            Success = false,
            Environment = _env.EnvironmentName,
            Action = action,
            Reason = reason,
            Warning = "UAT test harness only"
        });
    }

    private TestHarnessActionResult Result(string action, string? entityId, object? before, object? after) => new()
    {
        Success = true,
        Environment = _env.EnvironmentName,
        Action = action,
        EntityId = entityId,
        Before = before,
        After = after
    };

    // ─── 1. Run once (real orchestrator) ────────────────────────────

    /// <summary>
    /// Runs the recurring engine once via the real orchestrator
    /// (TriggeredBy=AdminManual, run-lock + BillingRunLog reused). A real
    /// (dryRun=false) run additionally requires AllowRealChargeRun=true.
    /// </summary>
    [HttpPost("run-once")]
    public async Task<IActionResult> RunOnce([FromBody] RunOnceRequest request, CancellationToken cancellationToken = default)
    {
        const string action = "run_once";
        request ??= new RunOnceRequest();

        var blocked = Guard(action, _harness.AllowRunOnce, request.ConfirmationPhrase);
        if (blocked is not null) return blocked;

        if (!request.DryRun && !_harness.AllowRealChargeRun)
            return Blocked(action, "real_charge_disabled", StatusCodes.Status503ServiceUnavailable);

        var context = new RecurringBillingRunContext(
            RunId: Guid.NewGuid(),
            NowUtc: DateTime.UtcNow,
            DryRun: request.DryRun,
            TriggeredBy: BillingRunTrigger.AdminManual,
            MaxInvoicesPerRun: _autoSettings.MaxInvoicesPerRun,
            MaxChargesPerRun: _autoSettings.MaxChargesPerRun);

        _logger.LogInformation(
            "[recurring-billing][test-harness][run-once] actor={Actor} dryRun={DryRun} runId={RunId} note={Note}",
            _currentUser.UserId, request.DryRun, context.RunId, request.Note);

        var summary = await _orchestrator.RunAsync(context, cancellationToken);

        // Summary is counters-only — safe to return verbatim.
        var entityId = (summary.BillingRunLogId ?? summary.RunId).ToString();
        var before = new { request.DryRun, TriggeredBy = nameof(BillingRunTrigger.AdminManual) };
        return Ok(Result(action, entityId, before, summary));
    }

    // ─── 2. Make a schedule generation-due (NextInvoiceDateUtc only) ─

    /// <summary>Sets ONLY <c>NextInvoiceDateUtc</c> so the real generator (Stage 1) picks the schedule up next run.</summary>
    [HttpPost("schedules/{scheduleId:guid}/make-generation-due")]
    public async Task<IActionResult> MakeGenerationDue(
        Guid scheduleId, [FromBody] MakeGenerationDueRequest request, CancellationToken cancellationToken = default)
    {
        const string action = "make_generation_due";
        request ??= new MakeGenerationDueRequest();

        var blocked = Guard(action, _harness.AllowDateNudges, request.ConfirmationPhrase);
        if (blocked is not null) return blocked;

        var schedule = await _dbContext.ServiceBillingSchedules
            .FirstOrDefaultAsync(s => s.Id == scheduleId, cancellationToken);
        if (schedule is null)
            return NotFound(new { Success = false, Action = action, Message = "Schedule not found." });

        var before = new { schedule.NextInvoiceDateUtc, schedule.NextDueDateUtc, Status = schedule.Status.ToString() };

        schedule.NextInvoiceDateUtc = request.NextInvoiceDateUtc ?? DateTime.UtcNow;
        schedule.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var after = new { schedule.NextInvoiceDateUtc, schedule.NextDueDateUtc, Status = schedule.Status.ToString() };

        _logger.LogInformation(
            "[recurring-billing][test-harness][nudge-schedule] actor={Actor} scheduleId={ScheduleId} nextInvoiceDateUtc={Value} note={Note}",
            _currentUser.UserId, scheduleId, schedule.NextInvoiceDateUtc, request.Note);

        return Ok(Result(action, scheduleId.ToString(), before, after));
    }

    // ─── 3. Make an invoice due now (DueAtUtc only) ─────────────────

    /// <summary>Sets ONLY <c>DueAtUtc</c> so the real Stage 2 charges it next run (or, back-dated beyond grace, Stage 4 detects it).</summary>
    [HttpPost("invoices/{invoiceId:guid}/make-due-now")]
    public async Task<IActionResult> MakeInvoiceDueNow(
        Guid invoiceId, [FromBody] MakeInvoiceDueRequest request, CancellationToken cancellationToken = default)
    {
        const string action = "make_invoice_due_now";
        request ??= new MakeInvoiceDueRequest();

        var blocked = Guard(action, _harness.AllowDateNudges, request.ConfirmationPhrase);
        if (blocked is not null) return blocked;

        var invoice = await _dbContext.Invoices
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);
        if (invoice is null)
            return NotFound(new { Success = false, Action = action, Message = "Invoice not found." });

        var before = new { invoice.DueAtUtc, Status = invoice.Status.ToString(), invoice.BalanceDue };

        invoice.DueAtUtc = request.DueAtUtc ?? DateTime.UtcNow;
        invoice.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var after = new { invoice.DueAtUtc, Status = invoice.Status.ToString(), invoice.BalanceDue };

        _logger.LogInformation(
            "[recurring-billing][test-harness][nudge-invoice] actor={Actor} invoiceId={InvoiceId} dueAtUtc={Value} note={Note}",
            _currentUser.UserId, invoiceId, invoice.DueAtUtc, request.Note);

        return Ok(Result(action, invoiceId.ToString(), before, after));
    }

    // ─── 4. Make a retry due now (ScheduledForUtc only, Pending only) ─

    /// <summary>Sets ONLY <c>ScheduledForUtc</c> on a Pending attempt so the real Stage 3 retries it next run.</summary>
    [HttpPost("retries/{attemptId:guid}/make-due-now")]
    public async Task<IActionResult> MakeRetryDueNow(
        Guid attemptId, [FromBody] MakeRetryDueRequest request, CancellationToken cancellationToken = default)
    {
        const string action = "make_retry_due_now";
        request ??= new MakeRetryDueRequest();

        var blocked = Guard(action, _harness.AllowDateNudges, request.ConfirmationPhrase);
        if (blocked is not null) return blocked;

        var attempt = await _dbContext.PaymentRetryAttempts
            .FirstOrDefaultAsync(a => a.Id == attemptId, cancellationToken);
        if (attempt is null)
            return NotFound(new { Success = false, Action = action, Message = "Retry attempt not found." });

        if (attempt.Status != PaymentRetryAttemptStatus.Pending)
        {
            return StatusCode(StatusCodes.Status409Conflict, new
            {
                Success = false,
                Environment = _env.EnvironmentName,
                Action = action,
                Message = $"Retry attempt is {attempt.Status}, not Pending — only Pending attempts can be nudged.",
                Warning = "UAT test harness only"
            });
        }

        var before = new { attempt.ScheduledForUtc, Status = attempt.Status.ToString(), attempt.AttemptNumber };

        attempt.ScheduledForUtc = request.ScheduledForUtc ?? DateTime.UtcNow;
        attempt.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var after = new { attempt.ScheduledForUtc, Status = attempt.Status.ToString(), attempt.AttemptNumber };

        _logger.LogInformation(
            "[recurring-billing][test-harness][nudge-retry] actor={Actor} attemptId={AttemptId} scheduledForUtc={Value} note={Note}",
            _currentUser.UserId, attemptId, attempt.ScheduledForUtc, request.Note);

        return Ok(Result(action, attemptId.ToString(), before, after));
    }
}

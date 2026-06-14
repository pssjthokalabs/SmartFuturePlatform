using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Payments.BillingOps.Dtos;
using SmartFuture.Application.Payments.Recurring;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Payments.BillingOps;

/// <summary>
/// Implements manual service-invoice creation. See <see cref="IManualInvoiceService"/>.
/// The Phase 0B unique index on (ServiceBillingScheduleId, PeriodStartUtc)
/// makes a same-schedule+period duplicate impossible at the DB, so a forced
/// duplicate is created SCHEDULE-DETACHED (ScheduleId/Period nulled), with
/// the intended period preserved in AdminNotes + audit metadata. A
/// schedule-detached invoice is invisible to the recurring engine's Stage 2/4
/// (both require ServiceBillingScheduleId != null), so it is never auto-charged.
/// </summary>
public sealed class ManualInvoiceService : IManualInvoiceService
{
    private const string InvoiceNumberPrefix = "INV";
    private const int InvoiceNumberMaxAttempts = 5;

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _audit;
    private readonly IBillingNotificationService _notifications;
    private readonly AutoBillingSettings _autoSettings;
    private readonly BillingOpsSettings _opsSettings;
    private readonly ILogger<ManualInvoiceService> _logger;

    public ManualInvoiceService(
        IAppDbContext dbContext,
        IAuditService audit,
        IBillingNotificationService notifications,
        IOptions<AutoBillingSettings> autoSettings,
        IOptions<BillingOpsSettings> opsSettings,
        ILogger<ManualInvoiceService> logger)
    {
        _dbContext = dbContext;
        _audit = audit;
        _notifications = notifications;
        _autoSettings = autoSettings.Value;
        _opsSettings = opsSettings.Value;
        _logger = logger;
    }

    public async Task<Result<ManualServiceInvoiceResultDto>> CreateManualServiceInvoiceAsync(
        ManualServiceInvoiceRequestDto request,
        Guid? actorUserId, string? ipAddress, string? userAgent,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            return Result<ManualServiceInvoiceResultDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

        // ── Confirmation phrase + force gate ──
        if (request.Force)
        {
            if (string.IsNullOrWhiteSpace(request.ForceReason))
                return Result<ManualServiceInvoiceResultDto>.Failure(ErrorCodes.BAD_REQUEST, "A force reason is required to force-create a duplicate invoice.");
            if (!string.Equals(request.ConfirmationPhrase, _opsSettings.ForceCreateConfirmationPhrase, StringComparison.Ordinal))
                return Result<ManualServiceInvoiceResultDto>.Failure(ErrorCodes.BAD_REQUEST, "Incorrect force confirmation phrase.");
        }
        else
        {
            if (!string.Equals(request.ConfirmationPhrase, _opsSettings.ManualInvoiceConfirmationPhrase, StringComparison.Ordinal))
                return Result<ManualServiceInvoiceResultDto>.Failure(ErrorCodes.BAD_REQUEST, "Incorrect confirmation phrase.");
        }

        // ── Basic field validation ──
        if (request.Amount <= 0m)
            return Result<ManualServiceInvoiceResultDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Amount must be greater than zero.");
        if (request.PeriodEndUtc <= request.PeriodStartUtc)
            return Result<ManualServiceInvoiceResultDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Period end must be after period start.");

        // ── Resolve + validate the schedule (tracked — may advance it) ──
        var schedule = await _dbContext.ServiceBillingSchedules
            .FirstOrDefaultAsync(s => s.Id == request.ServiceBillingScheduleId, cancellationToken);
        if (schedule is null)
            return Result<ManualServiceInvoiceResultDto>.Failure(ErrorCodes.NOT_FOUND, "Service billing schedule not found.");

        if (schedule.NetworkAccountId != request.NetworkAccountId || schedule.UserId != request.CustomerId)
            return Result<ManualServiceInvoiceResultDto>.Failure(ErrorCodes.BAD_REQUEST,
                "Schedule does not match the supplied customer / network account.");

        var networkAccount = await _dbContext.NetworkAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == request.NetworkAccountId, cancellationToken);
        if (networkAccount is null)
            return Result<ManualServiceInvoiceResultDto>.Failure(ErrorCodes.NOT_FOUND, "Network account not found.");

        // ── Duplicate-period detection (exact OR overlap) ──
        var existing = await _dbContext.Invoices
            .AsNoTracking()
            .Where(i => i.ServiceBillingScheduleId == schedule.Id
                     && (i.PeriodStartUtc == request.PeriodStartUtc
                         || (i.PeriodStartUtc != null && i.PeriodEndUtc != null
                             && i.PeriodStartUtc < request.PeriodEndUtc
                             && i.PeriodEndUtc > request.PeriodStartUtc)))
            .OrderByDescending(i => i.CreatedAtUtc)
            .Select(i => new DuplicatePeriodInvoiceDto
            {
                Id = i.Id,
                InvoiceNumber = i.InvoiceNumber,
                Status = i.Status,
                PeriodStartUtc = i.PeriodStartUtc,
                PeriodEndUtc = i.PeriodEndUtc,
                TotalAmount = i.TotalAmount,
                BalanceDue = i.BalanceDue
            })
            .FirstOrDefaultAsync(cancellationToken);

        // ── Block unless forced ──
        if (existing is not null && !request.Force)
        {
            _logger.LogWarning(
                "[billing-ops][manual-invoice][blocked] schedule {ScheduleId} period {PeriodStart:o} duplicates invoice {InvoiceNumber} ({InvoiceId}).",
                schedule.Id, request.PeriodStartUtc, existing.InvoiceNumber, existing.Id);

            var blocked = new ManualServiceInvoiceResultDto
            {
                Code = "duplicate_period_invoice",
                Message = "An invoice already exists for this billing period.",
                CanForceCreate = true,
                ForceRequiredConfirmationPhrase = _opsSettings.ForceCreateConfirmationPhrase,
                ExistingInvoice = existing
            };
            return Result<ManualServiceInvoiceResultDto>.Failure(ErrorCodes.CONFLICT, blocked.Message, blocked);
        }

        var forced = request.Force;
        var currency = string.IsNullOrWhiteSpace(schedule.CurrencyCode) ? "ZAR" : schedule.CurrencyCode;
        var now = DateTime.UtcNow;

        var invoiceNumber = await AllocateInvoiceNumberAsync(now, cancellationToken);
        if (invoiceNumber is null)
            return Result<ManualServiceInvoiceResultDto>.Failure(ErrorCodes.EXCEPTION, "Could not allocate a unique invoice number. Please retry.");

        var description = string.IsNullOrWhiteSpace(networkAccount.PackageName)
            ? $"Service subscription — {request.PeriodStartUtc:yyyy-MM-dd} to {request.PeriodEndUtc:yyyy-MM-dd}"
            : $"{networkAccount.PackageName} — {request.PeriodStartUtc:yyyy-MM-dd} to {request.PeriodEndUtc:yyyy-MM-dd}";

        var invoice = new Invoice
        {
            InvoiceNumber = invoiceNumber,
            OrderId = schedule.OrderId,
            // Forced duplicate → schedule-detached (respects the Phase 0B
            // unique index AND keeps the recurring engine from touching it).
            ServiceBillingScheduleId = forced ? null : schedule.Id,
            PeriodStartUtc = forced ? null : request.PeriodStartUtc,
            PeriodEndUtc = forced ? null : request.PeriodEndUtc,
            Status = InvoiceStatus.Issued,
            SubtotalAmount = request.Amount,
            TaxAmount = 0m,
            TotalAmount = request.Amount,
            AmountPaid = 0m,
            BalanceDue = request.Amount,
            CurrencyCode = currency,
            IssuedAtUtc = now,
            DueAtUtc = request.DueAtUtc,
            Notes = "Manual recurring service invoice — created by an admin via Billing Ops.",
            AdminNotes = BuildAdminNotes(request, schedule.Id, forced, existing)
        };
        _dbContext.Invoices.Add(invoice);

        _dbContext.InvoiceLineItems.Add(new InvoiceLineItem
        {
            Invoice = invoice,
            LineType = InvoiceLineItemType.ServicePackage,
            Description = description,
            Quantity = 1,
            UnitAmount = request.Amount,
            TotalAmount = request.Amount,
            SortOrder = 0
        });

        // ── Schedule advance: ONLY a normal create for the exact next period ──
        var scheduleAdvanced = false;
        if (!forced
            && schedule.NextDueDateUtc.HasValue
            && request.PeriodStartUtc == schedule.NextDueDateUtc.Value)
        {
            var interval = BillingCycleCalculator.IntervalFor(schedule.BillingCycle);
            if (interval is not null && interval.Value > TimeSpan.Zero)
            {
                var daysBeforeDue = Math.Max(0, _autoSettings.GenerateInvoicesDaysBeforeDue);
                schedule.CurrentPeriodStartUtc = request.PeriodStartUtc;
                schedule.CurrentPeriodEndUtc = request.PeriodEndUtc;
                schedule.NextDueDateUtc = request.PeriodEndUtc;
                schedule.NextInvoiceDateUtc = request.PeriodEndUtc - TimeSpan.FromDays(daysBeforeDue);
                schedule.LastInvoicedPeriodEndUtc = request.PeriodEndUtc;
                schedule.LastInvoiceId = invoice.Id;
                schedule.UpdatedAtUtc = now;
                scheduleAdvanced = true;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // ── Audit (always) ──
        await WriteAuditAsync(request, schedule.Id, invoice, forced, existing, actorUserId, ipAddress, userAgent, cancellationToken);

        _logger.LogInformation(
            "[billing-ops][manual-invoice][{Tag}] invoice {InvoiceNumber} ({InvoiceId}) schedule {ScheduleId} detached={Detached} advanced={Advanced} amount {Amount} {Currency}.",
            forced ? "forced" : "created", invoice.InvoiceNumber, invoice.Id, schedule.Id, forced, scheduleAdvanced, request.Amount, currency);

        // ── Notify (normal create only, mirrors the generator; gated + best-effort) ──
        if (!forced)
        {
            await _notifications.NotifyInvoiceGeneratedAsync(
                invoice.Id, invoice.InvoiceNumber, schedule.UserId,
                request.Amount, request.DueAtUtc, currency, cancellationToken);
        }

        var result = new ManualServiceInvoiceResultDto
        {
            Code = forced ? "force_created" : "created",
            Message = forced
                ? "Forced duplicate service invoice created (schedule-detached)."
                : "Service invoice created.",
            InvoiceId = invoice.Id,
            InvoiceNumber = invoice.InvoiceNumber,
            Status = invoice.Status,
            ScheduleDetached = forced,
            ScheduleAdvanced = scheduleAdvanced,
            InvoiceLink = $"/admin/invoices/{invoice.Id}"
        };
        return Result<ManualServiceInvoiceResultDto>.Success(result, result.Message);
    }

    private static string BuildAdminNotes(
        ManualServiceInvoiceRequestDto request, Guid scheduleId, bool forced, DuplicatePeriodInvoiceDto? existing)
    {
        if (!forced)
            return $"Manual service invoice for schedule {scheduleId}, period {request.PeriodStartUtc:o}–{request.PeriodEndUtc:o}.";

        var dup = existing is not null ? $" Duplicate of invoice {existing.InvoiceNumber} ({existing.Id})." : string.Empty;
        return $"FORCE-CREATED schedule-detached manual invoice. Intended schedule {scheduleId}, "
             + $"intended period {request.PeriodStartUtc:o}–{request.PeriodEndUtc:o}.{dup} Reason: {request.ForceReason}";
    }

    private async Task WriteAuditAsync(
        ManualServiceInvoiceRequestDto request, Guid scheduleId, Invoice invoice, bool forced,
        DuplicatePeriodInvoiceDto? existing, Guid? actorUserId, string? ipAddress, string? userAgent,
        CancellationToken cancellationToken)
    {
        // Non-sensitive metadata only — no token / card / gateway payload / signature.
        var metadata = JsonSerializer.Serialize(new
        {
            scheduleId,
            intendedPeriodStartUtc = request.PeriodStartUtc,
            intendedPeriodEndUtc = request.PeriodEndUtc,
            dueAtUtc = request.DueAtUtc,
            amount = request.Amount,
            force = forced,
            forceReason = forced ? request.ForceReason : null,
            scheduleDetached = forced,
            duplicateOfInvoiceId = existing?.Id
        }, MetadataJsonOptions);

        await _audit.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = actorUserId,
            ActorType = AuditActorType.Admin,
            ActionType = forced
                ? AuditActionType.ForcedDuplicateServiceInvoiceCreated
                : AuditActionType.ManualServiceInvoiceCreated,
            EntityType = AuditEntityType.Invoice,
            EntityId = invoice.Id,
            EntityName = invoice.InvoiceNumber,
            Summary = forced
                ? $"Force-created schedule-detached service invoice {invoice.InvoiceNumber} for {request.Amount:0.00} (schedule {scheduleId})."
                : $"Created manual service invoice {invoice.InvoiceNumber} for {request.Amount:0.00} (schedule {scheduleId}).",
            MetadataJson = metadata,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            IsSuccess = true
        }, cancellationToken);
    }

    private async Task<string?> AllocateInvoiceNumberAsync(DateTime now, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < InvoiceNumberMaxAttempts; attempt++)
        {
            var candidate = BillingNumberGenerator.BuildCandidate(InvoiceNumberPrefix, now);
            var exists = await _dbContext.Invoices.AnyAsync(i => i.InvoiceNumber == candidate, cancellationToken);
            if (!exists) return candidate;
        }
        return null;
    }
}

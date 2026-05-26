using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.NetworkAccounts;
using SmartFuture.Application.NetworkAccounts.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Application.ServiceChanges.Dtos;
using SmartFuture.Domain.Billing;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.ServiceChanges;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Enums.ServiceChanges;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.ServiceChanges;

/// <summary>
/// Phase 51 — customer-initiated upgrade/downgrade workflow.
///
/// Upgrades take a customer's pro-rata charge today (mocked via Ozow
/// in UAT, real gateway later) and swap the underlying
/// <see cref="NetworkAccount"/> as soon as the Payment is Completed.
/// Downgrades record-only with status Scheduled; the swap is applied
/// later by an admin via <see cref="AdminProcessAsync"/> or by a
/// future recurring-invoice job.
///
/// The package swap itself goes through
/// <see cref="INetworkAccountService.AdminChangePackageAsync"/> so
/// auditing + provisioner side-effects stay in one place.
/// </summary>
public class ServiceChangeRequestService : IServiceChangeRequestService
{
    private const string RequestNumberPrefix       = "SCR";
    private const int    RequestNumberSuffixLength = 6;
    private const int    RequestNumberMaxAttempts  = 5;

    private static readonly ServiceChangeStatus[] CustomerCancellableStatuses =
    {
        ServiceChangeStatus.PendingPayment,
        ServiceChangeStatus.Scheduled
    };

    // Statuses for which an account already has an in-flight request,
    // blocking a new one from being filed against the same service.
    private static readonly ServiceChangeStatus[] InFlightStatuses =
    {
        ServiceChangeStatus.PendingPayment,
        ServiceChangeStatus.Scheduled
    };

    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly INetworkAccountService _networkAccountService;
    private readonly PaymentSettings _paymentSettings;
    private readonly ILogger<ServiceChangeRequestService> _logger;

    public ServiceChangeRequestService(IAppDbContext dbContext, IAuditService auditService, ICurrentUserService currentUser, INetworkAccountService networkAccountService,
        IOptions<PaymentSettings> paymentSettings, ILogger<ServiceChangeRequestService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _currentUser = currentUser;
        _networkAccountService = networkAccountService;
        _paymentSettings = paymentSettings.Value;
        _logger = logger;
    }

    // ─── Customer endpoints ─────────────────────────────────────────────

    public async Task<Result<PagedResult<ServiceChangeRequestDto>>> GetMineAsync(ServiceChangeRequestFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<PagedResult<ServiceChangeRequestDto>>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            filter ??= new ServiceChangeRequestFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: currentUserId.Value);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching service change requests (customer)");
            return Result<PagedResult<ServiceChangeRequestDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while loading your change requests.");
        }
    }

    public Task<Result<ServiceChangeRequestDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var currentUserId = _currentUser.UserId;
        if (currentUserId is null || currentUserId == Guid.Empty)
            return Task.FromResult(Result<ServiceChangeRequestDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));
        return GetByIdInternalAsync(id, restrictToUserId: currentUserId, cancellationToken);
    }

    public async Task<Result<ServiceChangePreviewDto>> PreviewMineAsync(PreviewServiceChangeRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<ServiceChangePreviewDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (request is null || request.NetworkAccountId == Guid.Empty || request.RequestedPackageId == Guid.Empty)
                return Result<ServiceChangePreviewDto>.Failure(ErrorCodes.VALIDATION_ERROR, "NetworkAccountId and RequestedPackageId are required.");

            var loaded = await LoadEligibleContextAsync(request.NetworkAccountId, request.RequestedPackageId, currentUserId.Value, cancellationToken);
            if (!loaded.IsSuccess) return Result<ServiceChangePreviewDto>.Failure(loaded.Code!, loaded.Message!);

            var ctx = loaded.Data!;
            var calc = ProRataCalculator.Compute(
                ctx.Account, ctx.CurrentPrice, ctx.NewPackage.Price, ctx.CurrentCycle, DateTime.UtcNow);

            if (!calc.Eligible)
                return Result<ServiceChangePreviewDto>.Failure(ErrorCodes.CONFLICT, calc.IneligibilityReason ?? "Service is not currently billable.");

            var (type, mode) = ResolveTypeAndMode(ctx.CurrentPrice, ctx.NewPackage.Price);
            var dto = new ServiceChangePreviewDto
            {
                NetworkAccountId      = ctx.Account.Id,
                RequestedPackageId    = ctx.NewPackage.Id,
                CurrentPackageName    = ctx.Account.PackageName,
                CurrentMonthlyPrice   = ctx.CurrentPrice,
                RequestedPackageName  = ctx.NewPackage.Name,
                RequestedMonthlyPrice = ctx.NewPackage.Price,
                ChangeType            = type,
                EffectiveMode         = mode,
                DueTodayAmount        = calc.ProRataAmount,
                ProRataCycleDays      = calc.CycleDays,
                ProRataRemainingDays  = calc.RemainingDays,
                EffectiveDateUtc      = calc.EffectiveDateUtc,
                NextCycleAnchorUtc    = calc.NextCycleAnchorUtc,
                Summary               = BuildSummary(type, calc.ProRataAmount, ctx.NewPackage.Price, calc.NextCycleAnchorUtc)
            };
            return Result<ServiceChangePreviewDto>.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error computing service-change preview");
            return Result<ServiceChangePreviewDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while computing the change preview.");
        }
    }

    public async Task<Result<ServiceChangeRequestDto>> CreateMineAsync(CreateServiceChangeRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<ServiceChangeRequestDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (request is null || request.NetworkAccountId == Guid.Empty || request.RequestedPackageId == Guid.Empty)
                return Result<ServiceChangeRequestDto>.Failure(ErrorCodes.VALIDATION_ERROR, "NetworkAccountId and RequestedPackageId are required.");

            var loaded = await LoadEligibleContextAsync(request.NetworkAccountId, request.RequestedPackageId, currentUserId.Value, cancellationToken);
            if (!loaded.IsSuccess) return Result<ServiceChangeRequestDto>.Failure(loaded.Code!, loaded.Message!);
            var ctx = loaded.Data!;

            // Already-pending guard. Customer can only have one in-flight
            // request per service at a time — keeps the audit trail clean
            // and stops parallel charges.
            var inFlight = await _dbContext.ServiceChangeRequests
                .AnyAsync(r => r.NetworkAccountId == ctx.Account.Id && InFlightStatuses.Contains(r.Status), cancellationToken);
            if (inFlight)
                return Result<ServiceChangeRequestDto>.Failure(ErrorCodes.CONFLICT, "You already have a pending change request for this service.");

            var now  = DateTime.UtcNow;
            var calc = ProRataCalculator.Compute(ctx.Account, ctx.CurrentPrice, ctx.NewPackage.Price, ctx.CurrentCycle, now);
            if (!calc.Eligible)
                return Result<ServiceChangeRequestDto>.Failure(ErrorCodes.CONFLICT, calc.IneligibilityReason ?? "Service is not currently billable.");

            var (type, mode) = ResolveTypeAndMode(ctx.CurrentPrice, ctx.NewPackage.Price);

            var requestNumber = await GenerateUniqueRequestNumberAsync(now, cancellationToken)
                ?? $"{RequestNumberPrefix}-{now:yyyyMMdd}-MOCK";

            var entity = new ServiceChangeRequest
            {
                RequestNumber        = requestNumber,
                UserId               = currentUserId.Value,
                NetworkAccountId     = ctx.Account.Id,
                OrderId              = ctx.Account.OrderId,
                CurrentPackageId     = ctx.Account.Order?.ServicePackageId,
                CurrentPackageName   = ctx.Account.PackageName,
                CurrentMonthlyPrice  = ctx.CurrentPrice,
                CurrentBillingCycle  = ctx.CurrentCycle,
                RequestedPackageId   = ctx.NewPackage.Id,
                RequestedPackageName = ctx.NewPackage.Name,
                RequestedMonthlyPrice = ctx.NewPackage.Price,
                RequestedBillingCycle = ctx.NewPackage.BillingCycle,
                ChangeType           = type,
                EffectiveMode        = mode,
                Status               = type == ServiceChangeType.Upgrade ? ServiceChangeStatus.PendingPayment : ServiceChangeStatus.Scheduled,
                Source               = ServiceChangeSource.CustomerApp,
                ProRataAmount        = calc.ProRataAmount,
                ProRataCycleDays     = calc.CycleDays,
                ProRataRemainingDays = calc.RemainingDays,
                EffectiveDateUtc     = calc.EffectiveDateUtc,
                CustomerNotes        = Trim(request.CustomerNotes),
                LastStatusChangedByUserId = currentUserId
            };

            _dbContext.ServiceChangeRequests.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            // Upgrade path: raise the pro-rata invoice + (optionally)
            // settle it via mock-checkout. Only fires when the amount is
            // > 0 — a same-day upgrade with zero remaining days skips
            // billing entirely and just swaps the package.
            var mockCheckoutRequested = string.Equals(request.MockCheckoutPaymentProvider, "Ozow", StringComparison.OrdinalIgnoreCase);
            var mockCheckoutAttempted = type == ServiceChangeType.Upgrade
                && calc.ProRataAmount > 0m
                && _paymentSettings.MockCheckoutEnabled
                && mockCheckoutRequested;

            if (type == ServiceChangeType.Upgrade && calc.ProRataAmount > 0m)
            {
                var billing = await PersistProRataBillingAsync(entity, ctx.Account, request.MockCheckoutPaymentReference, mockCheckoutAttempted, now, cancellationToken);
                if (billing is not null)
                {
                    entity.InvoiceId = billing.InvoiceId;
                    entity.PaymentId = billing.PaymentId;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
            }

            await EmitAuditAsync(
                AuditActionType.OrderCreated, AuditActorType.User, entity,
                summary: $"Service change requested: {entity.RequestNumber} ({entity.ChangeType} from {entity.CurrentPackageName} to {entity.RequestedPackageName})",
                metadata: BuildMetadata(new
                {
                    networkAccountId = entity.NetworkAccountId,
                    type             = entity.ChangeType.ToString(),
                    mode             = entity.EffectiveMode.ToString(),
                    proRataAmount    = entity.ProRataAmount,
                    effectiveDateUtc = entity.EffectiveDateUtc
                }));

            // Auto-complete branch: upgrade + mock checkout settled the
            // payment server-side, so the swap can happen right now.
            // Downgrades + free-upgrade-day-of-cycle never enter this
            // branch — they wait for admin processing.
            if (type == ServiceChangeType.Upgrade
                && mockCheckoutAttempted
                && entity.PaymentId.HasValue)
            {
                var swap = await ApplyPackageSwapAsync(entity, $"Customer upgrade settled via {request.MockCheckoutPaymentProvider}. {entity.RequestNumber}", cancellationToken);
                if (!swap.IsSuccess)
                {
                    _logger.LogWarning("Swap step failed for {RequestNumber}: {Code} {Message}", entity.RequestNumber, swap.Code, swap.Message);
                    entity.Status        = ServiceChangeStatus.Failed;
                    entity.FailedAtUtc   = DateTime.UtcNow;
                    entity.FailureReason = swap.Message;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
            }
            else if (type == ServiceChangeType.Upgrade && calc.ProRataAmount <= 0m)
            {
                // Zero-cost upgrade (last-day-of-cycle edge). Swap
                // immediately, no payment step required.
                var swap = await ApplyPackageSwapAsync(entity, $"Customer upgrade with zero pro-rata. {entity.RequestNumber}", cancellationToken);
                if (!swap.IsSuccess)
                {
                    entity.Status        = ServiceChangeStatus.Failed;
                    entity.FailedAtUtc   = DateTime.UtcNow;
                    entity.FailureReason = swap.Message;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
            }

            var dto = await BuildDtoAsync(entity.Id, cancellationToken);
            return Result<ServiceChangeRequestDto>.Success(
                dto!,
                type == ServiceChangeType.Upgrade
                    ? "Upgrade request created."
                    : "Downgrade scheduled for your next billing cycle.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating service change request");
            return Result<ServiceChangeRequestDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while creating the change request.");
        }
    }

    public async Task<Result> CancelMineAsync(Guid id, string? cancellationReason = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");
            if (id == Guid.Empty)
                return Result.Failure(ErrorCodes.BAD_REQUEST, "Request id is required.");

            var entity = await _dbContext.ServiceChangeRequests.FirstOrDefaultAsync(r => r.Id == id && r.UserId == currentUserId.Value, cancellationToken);
            if (entity is null) return Result.Failure(ErrorCodes.NOT_FOUND, "Change request not found.");
            if (!CustomerCancellableStatuses.Contains(entity.Status))
                return Result.Failure(ErrorCodes.CONFLICT, $"Change requests in status '{entity.Status}' cannot be cancelled by the customer.");

            entity.Status                    = ServiceChangeStatus.Cancelled;
            entity.CancelledAtUtc            = DateTime.UtcNow;
            entity.CancellationReason        = Trim(cancellationReason) ?? entity.CancellationReason;
            entity.LastStatusChangedByUserId = currentUserId;
            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(AuditActionType.OrderStatusChanged, AuditActorType.User, entity,
                summary: $"Service change cancelled by customer: {entity.RequestNumber}",
                metadata: BuildMetadata(new { newStatus = entity.Status }));

            return Result.Success("Change request cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error cancelling service change request {Id}", id);
            return Result.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while cancelling the change request.");
        }
    }

    // ─── Admin endpoints ────────────────────────────────────────────────

    public async Task<Result<PagedResult<ServiceChangeRequestDto>>> SearchAdminAsync(ServiceChangeRequestFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new ServiceChangeRequestFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: null);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching service change requests (admin)");
            return Result<PagedResult<ServiceChangeRequestDto>>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while searching change requests.");
        }
    }

    public Task<Result<ServiceChangeRequestDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => GetByIdInternalAsync(id, restrictToUserId: null, cancellationToken);

    public async Task<Result<ServiceChangeRequestDto>> AdminProcessAsync(Guid id, AdminProcessServiceChangeRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty) return Result<ServiceChangeRequestDto>.Failure(ErrorCodes.BAD_REQUEST, "Request id is required.");
            var entity = await _dbContext.ServiceChangeRequests.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
            if (entity is null) return Result<ServiceChangeRequestDto>.Failure(ErrorCodes.NOT_FOUND, "Change request not found.");
            if (entity.Status != ServiceChangeStatus.PendingPayment && entity.Status != ServiceChangeStatus.Scheduled)
                return Result<ServiceChangeRequestDto>.Failure(ErrorCodes.CONFLICT, $"Requests in status '{entity.Status}' cannot be processed.");

            if (request is not null && !string.IsNullOrWhiteSpace(request.AdminNotes))
                entity.AdminNotes = request.AdminNotes.Trim();

            var swap = await ApplyPackageSwapAsync(entity, $"Admin processed {entity.RequestNumber}: swap to {entity.RequestedPackageName}.", cancellationToken);
            if (!swap.IsSuccess) return Result<ServiceChangeRequestDto>.Failure(swap.Code ?? ErrorCodes.EXCEPTION, swap.Message ?? "Failed to apply package change.");

            var dto = await BuildDtoAsync(entity.Id, cancellationToken);
            return Result<ServiceChangeRequestDto>.Success(dto!, "Change request applied.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing service change request {Id}", id);
            return Result<ServiceChangeRequestDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while processing the change request.");
        }
    }

    public async Task<Result<ServiceChangeRequestDto>> AdminRejectAsync(Guid id, AdminRejectServiceChangeRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty) return Result<ServiceChangeRequestDto>.Failure(ErrorCodes.BAD_REQUEST, "Request id is required.");
            var entity = await _dbContext.ServiceChangeRequests.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
            if (entity is null) return Result<ServiceChangeRequestDto>.Failure(ErrorCodes.NOT_FOUND, "Change request not found.");
            if (entity.Status != ServiceChangeStatus.PendingPayment && entity.Status != ServiceChangeStatus.Scheduled)
                return Result<ServiceChangeRequestDto>.Failure(ErrorCodes.CONFLICT, $"Requests in status '{entity.Status}' cannot be rejected.");

            entity.Status                    = ServiceChangeStatus.Rejected;
            entity.RejectedAtUtc             = DateTime.UtcNow;
            entity.RejectionReason           = Trim(request?.RejectionReason) ?? entity.RejectionReason;
            if (!string.IsNullOrWhiteSpace(request?.AdminNotes)) entity.AdminNotes = request!.AdminNotes!.Trim();
            entity.LastStatusChangedByUserId = _currentUser.UserId;
            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(AuditActionType.OrderStatusChanged, AuditActorType.Admin, entity,
                summary: $"Service change rejected: {entity.RequestNumber}",
                metadata: BuildMetadata(new { newStatus = entity.Status }));

            var dto = await BuildDtoAsync(entity.Id, cancellationToken);
            return Result<ServiceChangeRequestDto>.Success(dto!, "Change request rejected.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error rejecting service change request {Id}", id);
            return Result<ServiceChangeRequestDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while rejecting the change request.");
        }
    }

    public async Task<Result<ServiceChangeRequestDto>> AdminCancelAsync(Guid id, AdminCancelServiceChangeRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty) return Result<ServiceChangeRequestDto>.Failure(ErrorCodes.BAD_REQUEST, "Request id is required.");
            var entity = await _dbContext.ServiceChangeRequests.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
            if (entity is null) return Result<ServiceChangeRequestDto>.Failure(ErrorCodes.NOT_FOUND, "Change request not found.");
            if (entity.Status != ServiceChangeStatus.PendingPayment && entity.Status != ServiceChangeStatus.Scheduled)
                return Result<ServiceChangeRequestDto>.Failure(ErrorCodes.CONFLICT, $"Requests in status '{entity.Status}' cannot be cancelled.");

            entity.Status                    = ServiceChangeStatus.Cancelled;
            entity.CancelledAtUtc            = DateTime.UtcNow;
            entity.CancellationReason        = Trim(request?.CancellationReason) ?? entity.CancellationReason;
            if (!string.IsNullOrWhiteSpace(request?.AdminNotes)) entity.AdminNotes = request!.AdminNotes!.Trim();
            entity.LastStatusChangedByUserId = _currentUser.UserId;
            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(AuditActionType.OrderStatusChanged, AuditActorType.Admin, entity,
                summary: $"Service change cancelled by admin: {entity.RequestNumber}",
                metadata: BuildMetadata(new { newStatus = entity.Status }));

            var dto = await BuildDtoAsync(entity.Id, cancellationToken);
            return Result<ServiceChangeRequestDto>.Success(dto!, "Change request cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error cancelling (admin) service change request {Id}", id);
            return Result<ServiceChangeRequestDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while cancelling the change request.");
        }
    }

    public async Task<Result> OnInvoicePaidAsync(Guid invoiceId, Guid? paymentId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (invoiceId == Guid.Empty) return Result.Failure(ErrorCodes.BAD_REQUEST, "InvoiceId is required.");

            var entity = await _dbContext.ServiceChangeRequests
                .FirstOrDefaultAsync(r => r.InvoiceId == invoiceId && r.Status == ServiceChangeStatus.PendingPayment, cancellationToken);
            if (entity is null) return Result.Success("No matching pending change request.");

            if (paymentId.HasValue && !entity.PaymentId.HasValue) entity.PaymentId = paymentId.Value;
            await _dbContext.SaveChangesAsync(cancellationToken);

            var swap = await ApplyPackageSwapAsync(entity, $"Pro-rata invoice paid; auto-applied {entity.RequestNumber}.", cancellationToken);
            return swap.IsSuccess ? Result.Success("Change request completed.") : Result.Failure(swap.Code ?? ErrorCodes.EXCEPTION, swap.Message ?? "Swap step failed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in OnInvoicePaidAsync for invoice {InvoiceId}", invoiceId);
            return Result.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while completing the change request.");
        }
    }

    // ─── Internal helpers ───────────────────────────────────────────────

    private class EligibleContext
    {
        public NetworkAccount Account { get; init; } = null!;
        public decimal CurrentPrice { get; init; }
        public ServicePackageBillingCycle CurrentCycle { get; init; }
        public ServicePackage NewPackage { get; init; } = null!;
    }

    private async Task<Result<EligibleContext>> LoadEligibleContextAsync(Guid networkAccountId, Guid requestedPackageId, Guid currentUserId, CancellationToken cancellationToken)
    {
        var account = await _dbContext.NetworkAccounts
            .Include(a => a.Order)
            .FirstOrDefaultAsync(a => a.Id == networkAccountId, cancellationToken);
        if (account is null) return Result<EligibleContext>.Failure(ErrorCodes.NOT_FOUND, "Service not found.");

        var ownsAccount = account.Order != null && account.Order.UserId == currentUserId;
        if (!ownsAccount) return Result<EligibleContext>.Failure(ErrorCodes.FORBIDDEN, "Service does not belong to the current user.");

        if (account.Status != NetworkAccountStatus.Active)
            return Result<EligibleContext>.Failure(ErrorCodes.CONFLICT, $"Only active services can be upgraded or downgraded (current status: {account.Status}).");

        var newPackage = await _dbContext.ServicePackages.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == requestedPackageId, cancellationToken);
        if (newPackage is null) return Result<EligibleContext>.Failure(ErrorCodes.NOT_FOUND, "Selected package was not found.");

        if (newPackage.Status != ServicePackageStatus.Active)
            return Result<EligibleContext>.Failure(ErrorCodes.VALIDATION_ERROR, "Selected package is not active and cannot be requested.");

        if (newPackage.Id == account.Order?.ServicePackageId)
            return Result<EligibleContext>.Failure(ErrorCodes.VALIDATION_ERROR, "You're already on this package — pick a different one.");

        // Current price snapshot. Prefer the live NetworkAccount value
        // (set during provisioning + AdminChangePackage), fall back to
        // the originating order if the account is mid-init.
        var currentPrice = account.PackagePrice > 0m ? account.PackagePrice : (account.Order?.PackagePrice ?? 0m);
        var currentCycle = account.Order?.PackageBillingCycle ?? ServicePackageBillingCycle.Monthly;

        return Result<EligibleContext>.Success(new EligibleContext
        {
            Account      = account,
            CurrentPrice = currentPrice,
            CurrentCycle = currentCycle,
            NewPackage   = newPackage
        });
    }

    private static (ServiceChangeType type, ServiceChangeEffectiveMode mode) ResolveTypeAndMode(decimal currentPrice, decimal requestedPrice)
        => requestedPrice > currentPrice
            ? (ServiceChangeType.Upgrade,   ServiceChangeEffectiveMode.Immediate)
            : (ServiceChangeType.Downgrade, ServiceChangeEffectiveMode.NextCycle);

    private static string BuildSummary(ServiceChangeType type, decimal proRata, decimal newMonthly, DateTime? nextCycle)
    {
        if (type == ServiceChangeType.Upgrade)
        {
            return proRata > 0m
                ? $"You'll pay R{proRata:0.00} today for the upgrade difference. Your next monthly billing amount will be R{newMonthly:0.00}."
                : $"Nothing to pay today (you're on the last day of your cycle). Your next monthly billing amount will be R{newMonthly:0.00}.";
        }
        var when = nextCycle?.ToString("d MMM yyyy") ?? "your next billing cycle";
        return $"No charge today. Your downgrade to R{newMonthly:0.00}/month takes effect on {when}.";
    }

    /// <summary>
    /// Calls <see cref="INetworkAccountService.AdminChangePackageAsync"/>
    /// and updates the request's status/timestamps. Failures bubble back
    /// to the caller with the underlying error so audit + customer copy
    /// can stay consistent.
    /// </summary>
    private async Task<Result> ApplyPackageSwapAsync(ServiceChangeRequest entity, string adminNoteForSwap, CancellationToken cancellationToken)
    {
        var swap = await _networkAccountService.AdminChangePackageAsync(
            entity.NetworkAccountId,
            new AdminChangeNetworkAccountPackageRequestDto
            {
                NewServicePackageId = entity.RequestedPackageId,
                AdminNotes          = adminNoteForSwap
            },
            cancellationToken);

        if (!swap.IsSuccess) return Result.Failure(swap.Code ?? ErrorCodes.EXCEPTION, swap.Message ?? "Failed to apply package change.");

        entity.Status                    = ServiceChangeStatus.Completed;
        entity.AppliedAtUtc              = DateTime.UtcNow;
        entity.LastStatusChangedByUserId = _currentUser.UserId;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await EmitAuditAsync(AuditActionType.OrderStatusChanged, AuditActorType.Admin, entity,
            summary: $"Service change applied: {entity.RequestNumber} ({entity.CurrentPackageName} -> {entity.RequestedPackageName})",
            metadata: BuildMetadata(new { newStatus = entity.Status, appliedAtUtc = entity.AppliedAtUtc }));

        return Result.Success("Change applied.");
    }

    private class ProRataBillingResult
    {
        public Guid InvoiceId { get; init; }
        public Guid? PaymentId { get; init; }
    }

    /// <summary>
    /// Mints the pro-rata Invoice + a single ServicePackage line item.
    /// In UAT (mock-checkout) also writes a Completed Payment with the
    /// same amount so the auto-complete swap branch can fire. The
    /// invoice points at the underlying Order id so the existing
    /// billing reports continue to work.
    /// </summary>
    private async Task<ProRataBillingResult?> PersistProRataBillingAsync(ServiceChangeRequest scr, NetworkAccount account, string? mockReference, bool mockCheckoutAttempted, DateTime now, CancellationToken cancellationToken)
    {
        var orderId = account.OrderId;
        if (orderId == Guid.Empty)
        {
            _logger.LogWarning("Cannot raise pro-rata invoice for {RequestNumber}: NetworkAccount {AccountId} has no OrderId.", scr.RequestNumber, account.Id);
            return null;
        }

        var amount  = scr.ProRataAmount;
        var invoiceNumber = $"INV-{now:yyyyMMdd}-{BillingNumberGenerator.GenerateSuffix(6)}";
        var paymentNumber = $"PAY-{now:yyyyMMdd}-{BillingNumberGenerator.GenerateSuffix(6)}";

        try
        {
            var strategy = _dbContext.CreateExecutionStrategy();
            Guid invoiceId = Guid.Empty;
            Guid? paymentId = null;

            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _dbContext.BeginTransactionAsync(cancellationToken);

                var paid = mockCheckoutAttempted;
                var invoice = new Invoice
                {
                    InvoiceNumber  = invoiceNumber,
                    OrderId        = orderId,
                    Status         = paid ? InvoiceStatus.Paid : InvoiceStatus.Issued,
                    SubtotalAmount = amount,
                    TaxAmount      = 0m,
                    TotalAmount    = amount,
                    AmountPaid     = paid ? amount : 0m,
                    BalanceDue     = paid ? 0m : amount,
                    CurrencyCode   = "ZAR",
                    IssuedAtUtc    = now,
                    DueAtUtc       = now.AddDays(7),
                    PaidAtUtc      = paid ? now : null,
                    Notes          = $"Pro-rata charge for service change {scr.RequestNumber} ({scr.CurrentPackageName} -> {scr.RequestedPackageName}).",
                    ExternalReference = Truncate(mockReference, 100),
                    LastStatusChangedByUserId = scr.UserId
                };
                _dbContext.Invoices.Add(invoice);

                _dbContext.InvoiceLineItems.Add(new InvoiceLineItem
                {
                    Invoice     = invoice,
                    LineType    = InvoiceLineItemType.ServicePackage,
                    Description = $"Upgrade pro-rata: {scr.CurrentPackageName} -> {scr.RequestedPackageName} ({scr.ProRataRemainingDays} of {scr.ProRataCycleDays} days)",
                    Quantity    = 1,
                    UnitAmount  = amount,
                    TotalAmount = amount,
                    SortOrder   = 0
                });

                Payment? payment = null;
                if (paid)
                {
                    payment = new Payment
                    {
                        PaymentNumber    = paymentNumber,
                        Invoice          = invoice,
                        Status           = PaymentStatus.Completed,
                        Method           = PaymentMethodType.Gateway,
                        Amount           = amount,
                        CurrencyCode     = "ZAR",
                        PaidAtUtc        = now,
                        GatewayName      = "Ozow",
                        GatewayReference = Truncate(mockReference, 100),
                        Notes            = "Pro-rata upgrade — UAT mock payment (not a real charge).",
                        LastStatusChangedByUserId = scr.UserId
                    };
                    _dbContext.Payments.Add(payment);
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                invoiceId = invoice.Id;
                paymentId = payment?.Id;
            });

            return new ProRataBillingResult { InvoiceId = invoiceId, PaymentId = paymentId };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist pro-rata billing for {RequestNumber}", scr.RequestNumber);
            return null;
        }
    }

    private IQueryable<ServiceChangeRequest> BuildQuery(ServiceChangeRequestFilterRequestDto filter, Guid? restrictToUserId)
    {
        var query = _dbContext.ServiceChangeRequests
            .AsNoTracking()
            .Include(r => r.LastStatusChangedByUser)
            .Include(r => r.User)
            .Include(r => r.NetworkAccount)
            .Include(r => r.Order)
            .Include(r => r.Invoice)
            .Include(r => r.Payment)
            .AsQueryable();

        if (restrictToUserId.HasValue) query = query.Where(r => r.UserId == restrictToUserId.Value);
        else if (filter.UserId.HasValue) query = query.Where(r => r.UserId == filter.UserId.Value);

        if (filter.NetworkAccountId.HasValue)   query = query.Where(r => r.NetworkAccountId == filter.NetworkAccountId.Value);
        if (filter.OrderId.HasValue)            query = query.Where(r => r.OrderId == filter.OrderId.Value);
        if (filter.RequestedPackageId.HasValue) query = query.Where(r => r.RequestedPackageId == filter.RequestedPackageId.Value);
        if (filter.StatusFilter.HasValue)       query = query.Where(r => r.Status == filter.StatusFilter.Value);
        if (filter.ChangeType.HasValue)         query = query.Where(r => r.ChangeType == filter.ChangeType.Value);
        if (filter.EffectiveMode.HasValue)      query = query.Where(r => r.EffectiveMode == filter.EffectiveMode.Value);
        if (filter.Source.HasValue)             query = query.Where(r => r.Source == filter.Source.Value);
        if (filter.FromUtc.HasValue)            query = query.Where(r => r.CreatedAtUtc >= filter.FromUtc.Value);
        if (filter.ToUtc.HasValue)              query = query.Where(r => r.CreatedAtUtc <= filter.ToUtc.Value);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            query = query.Where(r =>
                EF.Functions.Like(r.RequestNumber, $"%{s}%") ||
                EF.Functions.Like(r.RequestedPackageName, $"%{s}%") ||
                EF.Functions.Like(r.CurrentPackageName, $"%{s}%"));
        }
        return query;
    }

    private async Task<Result<PagedResult<ServiceChangeRequestDto>>> ToPagedResultAsync(IQueryable<ServiceChangeRequest> query, ServiceChangeRequestFilterRequestDto filter, CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(r => r.CreatedAtUtc)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);

        var items = rows.Select(MapToDto).ToList();
        var paged = new PagedResult<ServiceChangeRequestDto>(items, filter.Page, filter.PageSize, totalCount);
        return Result<PagedResult<ServiceChangeRequestDto>>.Success(paged);
    }

    private async Task<Result<ServiceChangeRequestDto>> GetByIdInternalAsync(Guid id, Guid? restrictToUserId, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
            return Result<ServiceChangeRequestDto>.Failure(ErrorCodes.BAD_REQUEST, "Request id is required.");

        var query = _dbContext.ServiceChangeRequests
            .AsNoTracking()
            .Include(r => r.LastStatusChangedByUser)
            .Include(r => r.User)
            .Include(r => r.NetworkAccount)
            .Include(r => r.Order)
            .Include(r => r.Invoice)
            .Include(r => r.Payment)
            .Where(r => r.Id == id);

        if (restrictToUserId.HasValue) query = query.Where(r => r.UserId == restrictToUserId.Value);
        var entity = await query.FirstOrDefaultAsync(cancellationToken);
        if (entity is null) return Result<ServiceChangeRequestDto>.Failure(ErrorCodes.NOT_FOUND, "Change request not found.");
        return Result<ServiceChangeRequestDto>.Success(MapToDto(entity));
    }

    private async Task<ServiceChangeRequestDto?> BuildDtoAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await GetByIdInternalAsync(id, restrictToUserId: null, cancellationToken);
        return result.IsSuccess ? result.Data : null;
    }

    private static ServiceChangeRequestDto MapToDto(ServiceChangeRequest e) => new()
    {
        Id                          = e.Id,
        RequestNumber               = e.RequestNumber,
        UserId                      = e.UserId,
        UserEmail                   = e.User?.Email,
        UserFullName                = $"{e.User?.FirstName} {e.User?.LastName}".Trim(),
        NetworkAccountId            = e.NetworkAccountId,
        NetworkAccountNumber        = e.NetworkAccount?.AccountNumber,
        OrderId                     = e.OrderId,
        OrderNumber                 = e.Order?.OrderNumber,
        CurrentPackageId            = e.CurrentPackageId,
        CurrentPackageName          = e.CurrentPackageName,
        CurrentMonthlyPrice         = e.CurrentMonthlyPrice,
        CurrentBillingCycle         = e.CurrentBillingCycle,
        RequestedPackageId          = e.RequestedPackageId,
        RequestedPackageName        = e.RequestedPackageName,
        RequestedMonthlyPrice       = e.RequestedMonthlyPrice,
        RequestedBillingCycle       = e.RequestedBillingCycle,
        ChangeType                  = e.ChangeType,
        EffectiveMode               = e.EffectiveMode,
        Status                      = e.Status,
        Source                      = e.Source,
        ProRataAmount               = e.ProRataAmount,
        ProRataCycleDays            = e.ProRataCycleDays,
        ProRataRemainingDays        = e.ProRataRemainingDays,
        EffectiveDateUtc            = e.EffectiveDateUtc,
        InvoiceId                   = e.InvoiceId,
        InvoiceNumber               = e.Invoice?.InvoiceNumber,
        PaymentId                   = e.PaymentId,
        PaymentNumber               = e.Payment?.PaymentNumber,
        CustomerNotes               = e.CustomerNotes,
        AdminNotes                  = e.AdminNotes,
        CancellationReason          = e.CancellationReason,
        RejectionReason             = e.RejectionReason,
        FailureReason               = e.FailureReason,
        AppliedAtUtc                = e.AppliedAtUtc,
        CancelledAtUtc              = e.CancelledAtUtc,
        RejectedAtUtc               = e.RejectedAtUtc,
        FailedAtUtc                 = e.FailedAtUtc,
        LastStatusChangedByUserId   = e.LastStatusChangedByUserId,
        LastStatusChangedByUserEmail = e.LastStatusChangedByUser?.Email,
        CreatedAtUtc                = e.CreatedAtUtc,
        UpdatedAtUtc                = e.UpdatedAtUtc
    };

    private async Task<string?> GenerateUniqueRequestNumberAsync(DateTime now, CancellationToken cancellationToken)
    {
        for (var i = 0; i < RequestNumberMaxAttempts; i++)
        {
            var candidate = BillingNumberGenerator.BuildCandidate(RequestNumberPrefix, now, RequestNumberSuffixLength);
            var taken = await _dbContext.ServiceChangeRequests.AnyAsync(r => r.RequestNumber == candidate, cancellationToken);
            if (!taken) return candidate;
        }
        return null;
    }

    private async Task EmitAuditAsync(AuditActionType action, AuditActorType actor, ServiceChangeRequest entity, string summary, string? metadata)
    {
        try
        {
            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActionType   = action,
                ActorType    = actor,
                ActorUserId  = _currentUser.UserId,
                EntityType   = AuditEntityType.ServiceChangeRequest,
                EntityId     = entity.Id,
                EntityName   = entity.RequestNumber,
                Summary      = summary,
                MetadataJson = metadata
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit emission failed for service change {RequestNumber}", entity.RequestNumber);
        }
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= max ? value : value[..max];
    }

    private static string BuildMetadata(object payload) => JsonSerializer.Serialize(payload);
}

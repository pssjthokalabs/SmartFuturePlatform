using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Billing;

public class DebitOrderMandateService : IDebitOrderMandateService
{
    private const string DefaultCurrencyCode = "ZAR";

    private static readonly Regex Last4DigitsRegex = new(@"^\d{4}$", RegexOptions.Compiled);

    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<DebitOrderMandateService> _logger;

    public DebitOrderMandateService(IAppDbContext dbContext, IAuditService auditService, ICurrentUserService currentUser, ILogger<DebitOrderMandateService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<Result<PagedResult<DebitOrderMandateDto>>> SearchAdminAsync(DebitOrderMandateFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new DebitOrderMandateFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: null);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching debit order mandates (admin)");
            return Result<PagedResult<DebitOrderMandateDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching debit order mandates.");
        }
    }

    public async Task<Result<PagedResult<DebitOrderMandateDto>>> GetMineAsync(DebitOrderMandateFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<PagedResult<DebitOrderMandateDto>>.Failure(
                    ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            filter ??= new DebitOrderMandateFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: currentUserId.Value);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching debit order mandates (customer)");
            return Result<PagedResult<DebitOrderMandateDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching your debit order mandates.");
        }
    }

    public Task<Result<DebitOrderMandateDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => GetByIdInternalAsync(id, restrictToUserId: null, cancellationToken);

    public Task<Result<DebitOrderMandateDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var currentUserId = _currentUser.UserId;
        if (currentUserId is null || currentUserId == Guid.Empty)
            return Task.FromResult(
                Result<DebitOrderMandateDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));

        return GetByIdInternalAsync(id, restrictToUserId: currentUserId, cancellationToken);
    }

    private async Task<Result<DebitOrderMandateDto>> GetByIdInternalAsync(Guid id, Guid? restrictToUserId, CancellationToken cancellationToken)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<DebitOrderMandateDto>.Failure(ErrorCodes.BAD_REQUEST, "Mandate id is required.");

            var query = _dbContext.DebitOrderMandates
                .AsNoTracking()
                .Include(d => d.LastStatusChangedByUser)
                .Where(d => d.Id == id);

            if (restrictToUserId.HasValue)
                query = query.Where(d => d.UserId == restrictToUserId.Value);

            var entity = await query.FirstOrDefaultAsync(cancellationToken);

            return entity is null
                ? Result<DebitOrderMandateDto>.Failure(ErrorCodes.NOT_FOUND, "Debit order mandate not found.")
                : Result<DebitOrderMandateDto>.Success(MapToDto(entity));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching debit order mandate {Id}", id);
            return Result<DebitOrderMandateDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching the debit order mandate.");
        }
    }

    public async Task<Result<DebitOrderMandateDto>> CreateMineAsync(CreateDebitOrderMandateRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<DebitOrderMandateDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (request is null)
                return Result<DebitOrderMandateDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var validation = ValidateMutation(
                request.Amount, request.PreferredDebitDay, request.BankAccountLast4,
                request.StartDateUtc, request.EndDateUtc);
            if (validation is not null) return validation;

            if (request.OrderId.HasValue)
            {
                var ownsOrder = await _dbContext.Orders
                    .AnyAsync(o => o.Id == request.OrderId.Value && o.UserId == currentUserId.Value, cancellationToken);

                if (!ownsOrder)
                    return Result<DebitOrderMandateDto>.Failure(
                        ErrorCodes.NOT_FOUND, "Referenced order was not found for the current user.");
            }

            var customerProfileId = await _dbContext.CustomerProfiles
                .Where(p => p.UserId == currentUserId.Value)
                .Select(p => (Guid?)p.Id)
                .FirstOrDefaultAsync(cancellationToken);

            var entity = new DebitOrderMandate
            {
                UserId = currentUserId.Value,
                CustomerProfileId = customerProfileId,
                OrderId = request.OrderId,
                Status = DebitOrderMandateStatus.Pending,
                Frequency = request.Frequency,
                Amount = request.Amount,
                CurrencyCode = DefaultCurrencyCode,
                PreferredDebitDay = request.PreferredDebitDay,
                StartDateUtc = request.StartDateUtc,
                EndDateUtc = request.EndDateUtc,
                AccountHolderName = Trim(request.AccountHolderName),
                BankName = Trim(request.BankName),
                BankAccountLast4 = Trim(request.BankAccountLast4),
                BankAccountType = Trim(request.BankAccountType),
                MaskedAccountReference = Trim(request.MaskedAccountReference),
                GatewayMandateReference = Trim(request.GatewayMandateReference),
                ExternalReference = Trim(request.ExternalReference),
                Notes = Trim(request.Notes),
                LastStatusChangedByUserId = currentUserId
            };

            _dbContext.DebitOrderMandates.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitMandateAuditAsync(
                AuditActorType.User,
                entity,
                previous: (DebitOrderMandateStatus?)null,
                summary: $"Debit order mandate created (Status={entity.Status}, Frequency={entity.Frequency})");

            return Result<DebitOrderMandateDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Debit order mandate created.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating debit order mandate");
            return Result<DebitOrderMandateDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while creating the debit order mandate.");
        }
    }

    public async Task<Result<DebitOrderMandateDto>> AdminUpdateAsync(Guid id, AdminUpdateDebitOrderMandateRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<DebitOrderMandateDto>.Failure(ErrorCodes.BAD_REQUEST, "Mandate id is required.");

            if (request is null)
                return Result<DebitOrderMandateDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var validation = ValidateMutation(
                request.Amount, request.PreferredDebitDay, request.BankAccountLast4,
                request.StartDateUtc, request.EndDateUtc);
            if (validation is not null) return validation;

            var entity = await _dbContext.DebitOrderMandates
                .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

            if (entity is null)
                return Result<DebitOrderMandateDto>.Failure(ErrorCodes.NOT_FOUND, "Debit order mandate not found.");

            entity.Frequency = request.Frequency;
            entity.Amount = request.Amount;
            entity.PreferredDebitDay = request.PreferredDebitDay;
            entity.StartDateUtc = request.StartDateUtc;
            entity.EndDateUtc = request.EndDateUtc;
            entity.AccountHolderName = Trim(request.AccountHolderName);
            entity.BankName = Trim(request.BankName);
            entity.BankAccountLast4 = Trim(request.BankAccountLast4);
            entity.BankAccountType = Trim(request.BankAccountType);
            entity.MaskedAccountReference = Trim(request.MaskedAccountReference);
            entity.GatewayMandateReference = Trim(request.GatewayMandateReference);
            entity.ExternalReference = Trim(request.ExternalReference);
            entity.Notes = Trim(request.Notes);
            entity.AdminNotes = Trim(request.AdminNotes);

            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result<DebitOrderMandateDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Debit order mandate updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating debit order mandate {Id}", id);
            return Result<DebitOrderMandateDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the debit order mandate.");
        }
    }

    public async Task<Result<DebitOrderMandateDto>> AdminUpdateStatusAsync(Guid id, AdminUpdateDebitOrderMandateStatusDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<DebitOrderMandateDto>.Failure(ErrorCodes.BAD_REQUEST, "Mandate id is required.");

            if (request is null)
                return Result<DebitOrderMandateDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var entity = await _dbContext.DebitOrderMandates
                .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

            if (entity is null)
                return Result<DebitOrderMandateDto>.Failure(ErrorCodes.NOT_FOUND, "Debit order mandate not found.");

            var previous = entity.Status;
            var now = DateTime.UtcNow;

            entity.Status = request.Status;
            entity.LastStatusChangedByUserId = _currentUser.UserId;

            if (!string.IsNullOrWhiteSpace(request.AdminNotes))
                entity.AdminNotes = request.AdminNotes.Trim();

            switch (request.Status)
            {
                case DebitOrderMandateStatus.Active:
                    if (entity.ActivatedAtUtc is null) entity.ActivatedAtUtc = now;
                    break;
                case DebitOrderMandateStatus.Cancelled:
                    if (entity.CancelledAtUtc is null) entity.CancelledAtUtc = now;
                    break;
                case DebitOrderMandateStatus.Suspended:
                    if (entity.SuspendedAtUtc is null) entity.SuspendedAtUtc = now;
                    break;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            if (previous != entity.Status)
            {
                await EmitMandateAuditAsync(
                    AuditActorType.Admin,
                    entity,
                    previous: previous,
                    summary: $"Debit order mandate status changed: {previous} -> {entity.Status}");
            }

            return Result<DebitOrderMandateDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Debit order mandate status updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating debit order mandate status {Id}", id);
            return Result<DebitOrderMandateDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the debit order mandate status.");
        }
    }

    private IQueryable<DebitOrderMandate> BuildQuery(DebitOrderMandateFilterRequestDto filter, Guid? restrictToUserId)
    {
        var query = _dbContext.DebitOrderMandates
            .AsNoTracking()
            .Include(d => d.LastStatusChangedByUser)
            .AsQueryable();

        if (restrictToUserId.HasValue)
            query = query.Where(d => d.UserId == restrictToUserId.Value);
        else if (filter.UserId.HasValue)
            query = query.Where(d => d.UserId == filter.UserId.Value);

        if (filter.CustomerProfileId.HasValue)
            query = query.Where(d => d.CustomerProfileId == filter.CustomerProfileId.Value);

        if (filter.OrderId.HasValue)
            query = query.Where(d => d.OrderId == filter.OrderId.Value);

        if (filter.StatusFilter.HasValue)
            query = query.Where(d => d.Status == filter.StatusFilter.Value);

        if (filter.Frequency.HasValue)
            query = query.Where(d => d.Frequency == filter.Frequency.Value);

        if (filter.PreferredDebitDay.HasValue)
            query = query.Where(d => d.PreferredDebitDay == filter.PreferredDebitDay.Value);

        if (filter.StartFromUtc.HasValue)
            query = query.Where(d => d.StartDateUtc != null && d.StartDateUtc >= filter.StartFromUtc.Value);

        if (filter.StartToUtc.HasValue)
            query = query.Where(d => d.StartDateUtc != null && d.StartDateUtc <= filter.StartToUtc.Value);

        if (filter.FromUtc.HasValue)
            query = query.Where(d => d.CreatedAtUtc >= filter.FromUtc.Value);

        if (filter.ToUtc.HasValue)
            query = query.Where(d => d.CreatedAtUtc <= filter.ToUtc.Value);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            query = query.Where(d =>
                (d.AccountHolderName != null && EF.Functions.Like(d.AccountHolderName, $"%{s}%")) ||
                (d.BankName != null && EF.Functions.Like(d.BankName, $"%{s}%")) ||
                (d.MaskedAccountReference != null && EF.Functions.Like(d.MaskedAccountReference, $"%{s}%")) ||
                (d.GatewayMandateReference != null && EF.Functions.Like(d.GatewayMandateReference, $"%{s}%")) ||
                (d.ExternalReference != null && EF.Functions.Like(d.ExternalReference, $"%{s}%")));
        }

        return query;
    }

    private static async Task<Result<PagedResult<DebitOrderMandateDto>>> ToPagedResultAsync(IQueryable<DebitOrderMandate> query, DebitOrderMandateFilterRequestDto filter, CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(d => d.CreatedAtUtc)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(d => new DebitOrderMandateDto
            {
                Id = d.Id,
                UserId = d.UserId,
                CustomerProfileId = d.CustomerProfileId,
                OrderId = d.OrderId,
                Status = d.Status,
                Frequency = d.Frequency,
                Amount = d.Amount,
                CurrencyCode = d.CurrencyCode,
                PreferredDebitDay = d.PreferredDebitDay,
                StartDateUtc = d.StartDateUtc,
                EndDateUtc = d.EndDateUtc,
                ActivatedAtUtc = d.ActivatedAtUtc,
                CancelledAtUtc = d.CancelledAtUtc,
                SuspendedAtUtc = d.SuspendedAtUtc,
                AccountHolderName = d.AccountHolderName,
                BankName = d.BankName,
                BankAccountLast4 = d.BankAccountLast4,
                BankAccountType = d.BankAccountType,
                MaskedAccountReference = d.MaskedAccountReference,
                GatewayMandateReference = d.GatewayMandateReference,
                ExternalReference = d.ExternalReference,
                Notes = d.Notes,
                AdminNotes = d.AdminNotes,
                LastStatusChangedByUserId = d.LastStatusChangedByUserId,
                LastStatusChangedByUserEmail = d.LastStatusChangedByUser != null
                    ? d.LastStatusChangedByUser.Email
                    : null,
                CreatedAtUtc = d.CreatedAtUtc,
                UpdatedAtUtc = d.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        var paged = new PagedResult<DebitOrderMandateDto>(items, filter.Page, filter.PageSize, totalCount);
        return Result<PagedResult<DebitOrderMandateDto>>.Success(paged);
    }

    private async Task<DebitOrderMandate?> ReloadWithIncludesAsync(Guid id, CancellationToken cancellationToken)
        => await _dbContext.DebitOrderMandates
            .AsNoTracking()
            .Include(d => d.LastStatusChangedByUser)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    private static Result<DebitOrderMandateDto>? ValidateMutation(decimal? amount, int? preferredDebitDay, string? bankAccountLast4, DateTime? startDateUtc, DateTime? endDateUtc)
    {
        if (amount.HasValue && amount.Value < 0)
            return Result<DebitOrderMandateDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Amount cannot be negative.");

        if (preferredDebitDay.HasValue && (preferredDebitDay.Value < 1 || preferredDebitDay.Value > 31))
            return Result<DebitOrderMandateDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "PreferredDebitDay must be between 1 and 31.");

        if (!string.IsNullOrWhiteSpace(bankAccountLast4) && !Last4DigitsRegex.IsMatch(bankAccountLast4.Trim()))
            return Result<DebitOrderMandateDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "BankAccountLast4 must be exactly 4 numeric digits.");

        if (startDateUtc.HasValue && endDateUtc.HasValue && startDateUtc.Value > endDateUtc.Value)
            return Result<DebitOrderMandateDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "StartDateUtc cannot be after EndDateUtc.");

        return null;
    }

    private async Task EmitMandateAuditAsync(AuditActorType actorType, DebitOrderMandate entity, DebitOrderMandateStatus? previous, string summary)
    {
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = actorType,
            ActionType = AuditActionType.DebitOrderUpdated,
            EntityType = AuditEntityType.DebitOrder,
            EntityId = entity.Id,
            EntityName = entity.GatewayMandateReference ?? entity.MaskedAccountReference,
            Summary = summary,
            MetadataJson = BuildMetadata(new
            {
                previous,
                newStatus = entity.Status,
                frequency = entity.Frequency,
                preferredDebitDay = entity.PreferredDebitDay
            }),
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        });
    }

    private static string? BuildMetadata(object payload)
    {
        try { return JsonSerializer.Serialize(payload); }
        catch { return null; }
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DebitOrderMandateDto MapToDto(DebitOrderMandate d) => new()
    {
        Id = d.Id,
        UserId = d.UserId,
        CustomerProfileId = d.CustomerProfileId,
        OrderId = d.OrderId,
        Status = d.Status,
        Frequency = d.Frequency,
        Amount = d.Amount,
        CurrencyCode = d.CurrencyCode,
        PreferredDebitDay = d.PreferredDebitDay,
        StartDateUtc = d.StartDateUtc,
        EndDateUtc = d.EndDateUtc,
        ActivatedAtUtc = d.ActivatedAtUtc,
        CancelledAtUtc = d.CancelledAtUtc,
        SuspendedAtUtc = d.SuspendedAtUtc,
        AccountHolderName = d.AccountHolderName,
        BankName = d.BankName,
        BankAccountLast4 = d.BankAccountLast4,
        BankAccountType = d.BankAccountType,
        MaskedAccountReference = d.MaskedAccountReference,
        GatewayMandateReference = d.GatewayMandateReference,
        ExternalReference = d.ExternalReference,
        Notes = d.Notes,
        AdminNotes = d.AdminNotes,
        LastStatusChangedByUserId = d.LastStatusChangedByUserId,
        LastStatusChangedByUserEmail = d.LastStatusChangedByUser?.Email,
        CreatedAtUtc = d.CreatedAtUtc,
        UpdatedAtUtc = d.UpdatedAtUtc
    };
}

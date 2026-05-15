using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Customers.Admin.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Identity;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Enums.SupportTickets;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Customers.Admin;

public class AdminCustomerService : IAdminCustomerService
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private static readonly OrderStatus[] ActiveOrderStatuses =
    {
        OrderStatus.Confirmed, OrderStatus.AwaitingPayment, OrderStatus.PaymentReceived,
        OrderStatus.Provisioning, OrderStatus.Active
    };

    private static readonly InvoiceStatus[] UnpaidInvoiceStatuses =
    {
        InvoiceStatus.Issued, InvoiceStatus.PartiallyPaid, InvoiceStatus.Overdue
    };

    private static readonly SupportTicketStatus[] OpenSupportTicketStatuses =
    {
        SupportTicketStatus.Open, SupportTicketStatus.AwaitingCustomer, SupportTicketStatus.AwaitingAgent,
        SupportTicketStatus.InProgress, SupportTicketStatus.Reopened
    };

    private readonly IAppDbContext _dbContext;
    private readonly ILogger<AdminCustomerService> _logger;

    public AdminCustomerService(IAppDbContext dbContext, ILogger<AdminCustomerService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<Result<List<AdminCustomerListItemDto>>> SearchAsync(AdminCustomerFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new AdminCustomerFilterRequestDto();
            var page = Math.Max(1, filter.Page ?? 1);
            var pageSize = Math.Clamp(filter.PageSize ?? DefaultPageSize, 1, MaxPageSize);

            // A "customer" is any user with a CustomerProfile. This naturally
            // excludes admins; once role-table querying is available through
            // IAppDbContext, this can be tightened to "users in role
            // SystemRoles.Customer" for stronger guarantees.
            var query = _dbContext.Users.AsNoTracking()
                .Where(u => _dbContext.CustomerProfiles.Any(p => p.UserId == u.Id));

            var search = (filter.Search ?? string.Empty).Trim();
            if (search.Length > 0)
            {
                var s = search.ToLower();
                query = query.Where(u =>
                    (u.FirstName + " " + u.LastName).ToLower().Contains(s)
                    || (u.Email != null && u.Email.ToLower().Contains(s))
                    || (u.PhoneNumber != null && u.PhoneNumber.Contains(search)));
            }

            if (!string.IsNullOrWhiteSpace(filter.Status))
            {
                if (Enum.TryParse<UserAccountStatus>(NormalizeStatus(filter.Status), ignoreCase: true, out var statusEnum))
                    query = query.Where(u => u.AccountStatus == statusEnum);
            }

            // ServiceType / Balance filtering depends on related-entity
            // rollups. We could push these into the SQL via subqueries, but
            // for the small admin dataset it's cheaper and more readable to
            // project the rollups in the outer Select and then filter in
            // memory after the database round-trip.
            var projected = await query
                .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(u => new
                {
                    User = u,
                    Profile = _dbContext.CustomerProfiles.FirstOrDefault(p => p.UserId == u.Id),
                    ActiveOrder = _dbContext.Orders
                        .Where(o => o.UserId == u.Id && ActiveOrderStatuses.Contains(o.Status))
                        .OrderByDescending(o => o.CreatedAtUtc)
                        .Select(o => new { o.PackageName, o.PackageType })
                        .FirstOrDefault(),
                    Outstanding = _dbContext.Invoices
                        .Where(i => i.Order != null && i.Order.UserId == u.Id
                                 && UnpaidInvoiceStatuses.Contains(i.Status) && i.BalanceDue > 0)
                        .Sum(i => (decimal?)i.BalanceDue) ?? 0m
                })
                .ToListAsync(cancellationToken);

            var items = projected
                .Select(x => new AdminCustomerListItemDto
                {
                    Id = x.User.Id,
                    FirstName = x.User.FirstName,
                    LastName = x.User.LastName,
                    Email = x.User.Email,
                    PhoneNumber = x.User.PhoneNumber,
                    AccountStatus = MapStatus(x.User.AccountStatus),
                    Suburb = x.Profile?.Suburb,
                    City = x.Profile?.City,
                    Province = x.Profile?.Province,
                    ActivePackageName = x.ActiveOrder?.PackageName,
                    ServiceType = x.ActiveOrder == null ? null : x.ActiveOrder.PackageType.ToString(),
                    Outstanding = x.Outstanding,
                    CreatedAtUtc = x.User.CreatedAtUtc
                })
                .ToList();

            if (!string.IsNullOrWhiteSpace(filter.ServiceType))
            {
                var t = filter.ServiceType.Trim();
                items = items.Where(i => string.Equals(i.ServiceType, t, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (!string.IsNullOrWhiteSpace(filter.Balance))
            {
                if (filter.Balance.Equals("overdue", StringComparison.OrdinalIgnoreCase))
                    items = items.Where(i => i.Outstanding > 0).ToList();
                else if (filter.Balance.Equals("clear", StringComparison.OrdinalIgnoreCase))
                    items = items.Where(i => i.Outstanding == 0).ToList();
            }

            return Result<List<AdminCustomerListItemDto>>.Success(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching admin customers");
            return Result<List<AdminCustomerListItemDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while loading customers.");
        }
    }

    public async Task<Result<AdminCustomerDetailDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            var dto = await BuildDetailAsync(id, cancellationToken);
            return dto == null
                ? Result<AdminCustomerDetailDto>.Failure(ErrorCodes.NOT_FOUND, "We couldn't find that customer.")
                : Result<AdminCustomerDetailDto>.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error loading admin customer {Id}", id);
            return Result<AdminCustomerDetailDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while loading the customer.");
        }
    }

    public async Task<Result<AdminCustomerDetailDto>> UpdateAsync(Guid id, UpdateAdminCustomerRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            request ??= new UpdateAdminCustomerRequestDto();
            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
            if (user == null)
                return Result<AdminCustomerDetailDto>.Failure(ErrorCodes.NOT_FOUND, "We couldn't find that customer.");

            if (request.FirstName != null) user.FirstName = request.FirstName.Trim();
            if (request.LastName != null) user.LastName = request.LastName.Trim();
            user.UpdatedAtUtc = DateTime.UtcNow;

            var profile = await _dbContext.CustomerProfiles.FirstOrDefaultAsync(p => p.UserId == id, cancellationToken);
            if (profile == null)
            {
                profile = new Domain.Customers.CustomerProfile { UserId = id };
                _dbContext.CustomerProfiles.Add(profile);
            }

            if (request.IdNumber != null) profile.IdNumber = NullIfEmpty(request.IdNumber);
            if (request.AddressLine1 != null) profile.AddressLine1 = NullIfEmpty(request.AddressLine1);
            if (request.AddressLine2 != null) profile.AddressLine2 = NullIfEmpty(request.AddressLine2);
            if (request.Suburb != null) profile.Suburb = NullIfEmpty(request.Suburb);
            if (request.City != null) profile.City = NullIfEmpty(request.City);
            if (request.Province != null) profile.Province = NullIfEmpty(request.Province);
            if (request.PostalCode != null) profile.PostalCode = NullIfEmpty(request.PostalCode);
            if (request.Country != null) profile.Country = NullIfEmpty(request.Country);
            if (request.PreferredContactMethod != null) profile.PreferredContactMethod = NullIfEmpty(request.PreferredContactMethod);
            if (request.Notes != null) profile.Notes = NullIfEmpty(request.Notes);
            if (request.AcceptsMarketing.HasValue) profile.AcceptsMarketing = request.AcceptsMarketing.Value;
            if (request.AcceptsPaymentReminders.HasValue) profile.AcceptsPaymentReminders = request.AcceptsPaymentReminders.Value;
            if (request.AcceptsInstallationUpdates.HasValue) profile.AcceptsInstallationUpdates = request.AcceptsInstallationUpdates.Value;
            if (request.AcceptsNetworkAlerts.HasValue) profile.AcceptsNetworkAlerts = request.AcceptsNetworkAlerts.Value;
            profile.UpdatedAtUtc = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);

            var dto = await BuildDetailAsync(id, cancellationToken);
            return Result<AdminCustomerDetailDto>.Success(dto!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating admin customer {Id}", id);
            return Result<AdminCustomerDetailDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while saving changes.");
        }
    }

    public Task<Result<AdminCustomerDetailDto>> ActivateAsync(Guid id, CancellationToken cancellationToken = default)
        => ChangeStatusAsync(id, UserAccountStatus.Active, cancellationToken);

    public Task<Result<AdminCustomerDetailDto>> SuspendAsync(Guid id, CancellationToken cancellationToken = default)
        => ChangeStatusAsync(id, UserAccountStatus.Suspended, cancellationToken);

    private async Task<Result<AdminCustomerDetailDto>> ChangeStatusAsync(Guid id, UserAccountStatus status, CancellationToken cancellationToken)
    {
        try
        {
            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
            if (user == null)
                return Result<AdminCustomerDetailDto>.Failure(ErrorCodes.NOT_FOUND, "We couldn't find that customer.");

            user.AccountStatus = status;
            user.IsActive = status == UserAccountStatus.Active;
            user.UpdatedAtUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            var dto = await BuildDetailAsync(id, cancellationToken);
            return Result<AdminCustomerDetailDto>.Success(dto!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error changing customer {Id} status to {Status}", id, status);
            return Result<AdminCustomerDetailDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the customer.");
        }
    }

    private async Task<AdminCustomerDetailDto?> BuildDetailAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await _dbContext.Users.AsNoTracking()
            .Where(u => u.Id == id)
            .Where(u => _dbContext.CustomerProfiles.Any(p => p.UserId == u.Id))
            .Select(u => new
            {
                User = u,
                Profile = _dbContext.CustomerProfiles.FirstOrDefault(p => p.UserId == u.Id),
                ActiveOrder = _dbContext.Orders
                    .Where(o => o.UserId == u.Id && ActiveOrderStatuses.Contains(o.Status))
                    .OrderByDescending(o => o.CreatedAtUtc)
                    .Select(o => new { o.PackageName, o.PackageType })
                    .FirstOrDefault(),
                Outstanding = _dbContext.Invoices
                    .Where(i => i.Order != null && i.Order.UserId == u.Id
                             && UnpaidInvoiceStatuses.Contains(i.Status) && i.BalanceDue > 0)
                    .Sum(i => (decimal?)i.BalanceDue) ?? 0m,
                LastPayment = _dbContext.Payments
                    .Where(p => p.Status == PaymentStatus.Completed
                             && p.Invoice != null && p.Invoice.Order != null
                             && p.Invoice.Order.UserId == u.Id)
                    .OrderByDescending(p => p.PaidAtUtc ?? p.CreatedAtUtc)
                    .Select(p => new { p.Amount, p.PaidAtUtc })
                    .FirstOrDefault(),
                OpenTickets = _dbContext.SupportTickets
                    .Count(t => t.UserId == u.Id && OpenSupportTicketStatuses.Contains(t.Status))
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row == null) return null;

        return new AdminCustomerDetailDto
        {
            Id = row.User.Id,
            FirstName = row.User.FirstName,
            LastName = row.User.LastName,
            Email = row.User.Email,
            PhoneNumber = row.User.PhoneNumber,
            AccountStatus = MapStatus(row.User.AccountStatus),
            IdNumber = row.Profile?.IdNumber,
            AddressLine1 = row.Profile?.AddressLine1,
            AddressLine2 = row.Profile?.AddressLine2,
            Suburb = row.Profile?.Suburb,
            City = row.Profile?.City,
            Province = row.Profile?.Province,
            PostalCode = row.Profile?.PostalCode,
            Country = row.Profile?.Country,
            PreferredContactMethod = row.Profile?.PreferredContactMethod,
            AcceptsMarketing = row.Profile?.AcceptsMarketing ?? false,
            AcceptsPaymentReminders = row.Profile?.AcceptsPaymentReminders ?? true,
            AcceptsInstallationUpdates = row.Profile?.AcceptsInstallationUpdates ?? true,
            AcceptsNetworkAlerts = row.Profile?.AcceptsNetworkAlerts ?? true,
            Notes = row.Profile?.Notes,
            ActivePackageName = row.ActiveOrder?.PackageName,
            ServiceType = row.ActiveOrder == null ? null : row.ActiveOrder.PackageType.ToString(),
            Outstanding = row.Outstanding,
            LastPaymentAmount = row.LastPayment?.Amount,
            LastPaymentAtUtc = row.LastPayment?.PaidAtUtc,
            OpenTickets = row.OpenTickets,
            CreatedAtUtc = row.User.CreatedAtUtc,
            UpdatedAtUtc = row.User.UpdatedAtUtc
        };
    }

    private static string MapStatus(UserAccountStatus status) => status switch
    {
        UserAccountStatus.Active => "Active",
        UserAccountStatus.Inactive => "Inactive",
        UserAccountStatus.Suspended => "Suspended",
        UserAccountStatus.PendingVerification => "Pending Verification",
        _ => "Active"
    };

    private static string NormalizeStatus(string raw)
        => string.Equals(raw?.Trim(), "Pending Verification", StringComparison.OrdinalIgnoreCase)
            ? nameof(UserAccountStatus.PendingVerification)
            : (raw?.Trim() ?? string.Empty);

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

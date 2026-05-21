using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.OrderIntents.Dtos;
using SmartFuture.Application.Orders;
using SmartFuture.Application.Orders.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Application.ServicePackages.Dtos;
using SmartFuture.Domain.OrderIntents;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.OrderIntents;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.OrderIntents;

// Public pre-order intent service. Owns the lifecycle of the
// OrderIntent entity (Pending → Claimed → ConvertedToOrder) and
// delegates real order creation back to IOrderService so backend
// pricing/audit/notifications stay in one place.
public class OrderIntentService : IOrderIntentService
{
    // 24h is short enough that abandoned intents don't accumulate
    // forever, but long enough that a visitor can leave the site, sleep
    // on it, and come back the next day. Adjust via configuration if
    // that becomes necessary.
    private static readonly TimeSpan IntentLifetime = TimeSpan.FromHours(72);

    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IAppDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IOrderService _orderService;
    private readonly ILogger<OrderIntentService> _logger;

    public OrderIntentService(IAppDbContext dbContext, ICurrentUserService currentUser, IOrderService orderService, ILogger<OrderIntentService> logger)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _orderService = orderService;
        _logger = logger;
    }

    public async Task<Result<OrderIntentDto>> CreatePublicAsync(CreateOrderIntentRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request is null)
                return Result<OrderIntentDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            if (request.ServicePackageId == Guid.Empty)
                return Result<OrderIntentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "ServicePackageId is required.");

            // Real-time package validation. The website may be showing
            // fallback packages whose IDs don't match anything in the
            // database — those must fail here so the user is bounced
            // back to the live catalogue rather than landing on a
            // broken portal page.
            var package = await _dbContext.ServicePackages
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == request.ServicePackageId, cancellationToken);

            if (package is null)
                return Result<OrderIntentDto>.Failure(ErrorCodes.NOT_FOUND, "The selected package is no longer available. Please pick another package.");

            if (package.Status != ServicePackageStatus.Active)
                return Result<OrderIntentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "The selected package is not currently available for new orders.");

            var validation = ValidatePublicFields(request);
            if (validation is not null) return validation;

            var now = DateTime.UtcNow;
            var entity = new OrderIntent
            {
                IntentToken = GenerateIntentToken(),
                ServicePackageId = package.Id,
                FullName = Trim(request.FullName),
                Email = Trim(request.Email),
                PhoneNumber = Trim(request.PhoneNumber),
                AddressLine1 = Trim(request.AddressLine1),
                AddressLine2 = Trim(request.AddressLine2),
                Suburb = Trim(request.Suburb),
                City = Trim(request.City),
                Province = Trim(request.Province),
                PostalCode = Trim(request.PostalCode),
                Country = Trim(request.Country) ?? "South Africa",
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                GooglePlaceId = Trim(request.GooglePlaceId),
                MapProviderReference = Trim(request.MapProviderReference),
                RequestedInstallationDateUtc = request.RequestedInstallationDateUtc,
                CustomerNotes = Trim(request.CustomerNotes),
                Status = OrderIntentStatus.Pending,
                ExpiresAtUtc = now.Add(IntentLifetime),
                Source = TruncateSource(request.Source),
            };

            _dbContext.OrderIntents.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "OrderIntent {IntentId} created for package {PackageId} (expires {ExpiresAtUtc:o}).",
                entity.Id, entity.ServicePackageId, entity.ExpiresAtUtc);

            var dto = MapToDto(entity, MapPackageDto(package));
            return Result<OrderIntentDto>.Success(dto, "Order intent created.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating order intent.");
            return Result<OrderIntentDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while creating the order intent.");
        }
    }

    public async Task<Result<PublicOrderIntentPreviewDto>> GetPublicPreviewAsync(string intentToken, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(intentToken))
                return Result<PublicOrderIntentPreviewDto>.Failure(ErrorCodes.BAD_REQUEST, "Intent token is required.");

            var entity = await _dbContext.OrderIntents
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.IntentToken == intentToken, cancellationToken);

            if (entity is null)
                return Result<PublicOrderIntentPreviewDto>.Failure(
                    ErrorCodes.NOT_FOUND, "We couldn't find your saved order. It may have expired — please pick a package again.");

            await EnsureExpiredAsync(entity, cancellationToken);

            var package = await _dbContext.ServicePackages
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == entity.ServicePackageId, cancellationToken);

            var dto = new PublicOrderIntentPreviewDto
            {
                IntentToken = entity.IntentToken,
                Status = entity.Status,
                ExpiresAtUtc = entity.ExpiresAtUtc,
                Package = MapPackageDto(package),
                Email = entity.Email,
                FullName = entity.FullName,
                City = entity.City,
                Province = entity.Province,
            };

            return Result<PublicOrderIntentPreviewDto>.Success(dto, "OK");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error reading order intent preview.");
            return Result<PublicOrderIntentPreviewDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while looking up your order.");
        }
    }

    public async Task<Result<OrderIntentDto>> ClaimAsync(string intentToken, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<OrderIntentDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (string.IsNullOrWhiteSpace(intentToken))
                return Result<OrderIntentDto>.Failure(ErrorCodes.BAD_REQUEST, "Intent token is required.");

            var entity = await _dbContext.OrderIntents
                .FirstOrDefaultAsync(o => o.IntentToken == intentToken, cancellationToken);

            if (entity is null)
                return Result<OrderIntentDto>.Failure(
                    ErrorCodes.NOT_FOUND, "We couldn't find your saved order. It may have expired — please pick a package again.");

            await EnsureExpiredAsync(entity, cancellationToken);

            // Re-fetch after the possible expiry write — Status may have
            // flipped to Expired inside EnsureExpiredAsync.
            if (entity.Status == OrderIntentStatus.Expired)
                return Result<OrderIntentDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "Your saved order request has expired. Please pick a package again.");

            if (entity.Status == OrderIntentStatus.Cancelled)
                return Result<OrderIntentDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "This order request was cancelled.");

            if (entity.Status == OrderIntentStatus.ConvertedToOrder)
            {
                // Idempotent: if the same user already converted this
                // intent we still return success — the caller can
                // detect ConvertedOrderId and route to checkout. If a
                // *different* user converted it, that's a token-theft
                // signal and we reject.
                if (entity.ClaimedByUserId != currentUserId)
                    return Result<OrderIntentDto>.Failure(
                        ErrorCodes.FORBIDDEN, "This order request belongs to a different account.");

                var alreadyConvertedDto = MapToDto(entity, await ResolvePackageDtoAsync(entity.ServicePackageId, cancellationToken));
                return Result<OrderIntentDto>.Success(alreadyConvertedDto, "Order intent already converted.");
            }

            if (entity.Status == OrderIntentStatus.Claimed && entity.ClaimedByUserId != currentUserId)
                return Result<OrderIntentDto>.Failure(
                    ErrorCodes.FORBIDDEN, "This order request belongs to a different account.");

            if (entity.Status == OrderIntentStatus.Pending)
            {
                entity.Status = OrderIntentStatus.Claimed;
                entity.ClaimedByUserId = currentUserId;
                entity.ClaimedAtUtc = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "OrderIntent {IntentId} claimed by user {UserId}.",
                    entity.Id, currentUserId);
            }

            var dto = MapToDto(entity, await ResolvePackageDtoAsync(entity.ServicePackageId, cancellationToken));
            return Result<OrderIntentDto>.Success(dto, "Order intent claimed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error claiming order intent.");
            return Result<OrderIntentDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while claiming the order intent.");
        }
    }

    public async Task<Result<OrderDto>> ConvertAsync(string intentToken, ConvertOrderIntentRequestDto? overrides, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<OrderDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (string.IsNullOrWhiteSpace(intentToken))
                return Result<OrderDto>.Failure(ErrorCodes.BAD_REQUEST, "Intent token is required.");

            var entity = await _dbContext.OrderIntents
                .FirstOrDefaultAsync(o => o.IntentToken == intentToken, cancellationToken);

            if (entity is null)
                return Result<OrderDto>.Failure(
                    ErrorCodes.NOT_FOUND, "We couldn't find your saved order. It may have expired — please pick a package again.");

            await EnsureExpiredAsync(entity, cancellationToken);

            if (entity.Status == OrderIntentStatus.Expired)
                return Result<OrderDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "Your saved order request has expired. Please pick a package again.");

            if (entity.Status == OrderIntentStatus.Cancelled)
                return Result<OrderDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "This order request was cancelled.");

            // Idempotency: if the same user previously converted, return
            // the existing order rather than creating a duplicate.
            if (entity.Status == OrderIntentStatus.ConvertedToOrder)
            {
                if (entity.ClaimedByUserId != currentUserId)
                    return Result<OrderDto>.Failure(
                        ErrorCodes.FORBIDDEN, "This order request belongs to a different account.");

                if (entity.ConvertedOrderId is Guid existingId)
                {
                    var existing = await _orderService.GetMineByIdAsync(existingId, cancellationToken);
                    if (existing.IsSuccess && existing.Data is not null)
                        return Result<OrderDto>.Success(existing.Data, "Order already created for this intent.");
                }

                return Result<OrderDto>.Failure(
                    ErrorCodes.CONFLICT, "This order request has already been converted.");
            }

            if (entity.Status == OrderIntentStatus.Claimed && entity.ClaimedByUserId != currentUserId)
                return Result<OrderDto>.Failure(
                    ErrorCodes.FORBIDDEN, "This order request belongs to a different account.");

            // Auto-claim if the user jumps straight to convert without
            // having called claim first (the portal may skip claim when
            // it has all the data it needs from the prefill).
            if (entity.Status == OrderIntentStatus.Pending)
            {
                entity.Status = OrderIntentStatus.Claimed;
                entity.ClaimedByUserId = currentUserId;
                entity.ClaimedAtUtc = DateTime.UtcNow;
            }

            // Build the order request. Overrides win; the intent supplies
            // anything the override leaves blank. Backend pricing is
            // re-read inside OrderService.CreateMineAsync regardless of
            // what the website saw — the website cannot influence price.
            var createRequest = BuildCreateRequest(entity, overrides);

            var orderResult = await _orderService.CreateMineAsync(createRequest, cancellationToken);
            if (!orderResult.IsSuccess || orderResult.Data is null)
                return Result<OrderDto>.Failure(
                    orderResult.Code ?? ErrorCodes.EXCEPTION,
                    orderResult.Message);

            entity.Status = OrderIntentStatus.ConvertedToOrder;
            entity.ConvertedOrderId = orderResult.Data.Id;
            entity.ConvertedAtUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "OrderIntent {IntentId} converted to Order {OrderId} by user {UserId}.",
                entity.Id, orderResult.Data.Id, currentUserId);

            return Result<OrderDto>.Success(orderResult.Data, orderResult.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error converting order intent.");
            return Result<OrderDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while converting the order intent.");
        }
    }

    // ───────────────────────── helpers ─────────────────────────

    private static CreateOrderRequestDto BuildCreateRequest(OrderIntent intent, ConvertOrderIntentRequestDto? overrides)
    {
        var packageId = overrides?.ServicePackageId is Guid pid && pid != Guid.Empty ? pid : intent.ServicePackageId;

        return new CreateOrderRequestDto
        {
            ServicePackageId = packageId,
            FullName = overrides?.FullName ?? intent.FullName,
            Email = overrides?.Email ?? intent.Email,
            PhoneNumber = overrides?.PhoneNumber ?? intent.PhoneNumber,
            AddressLine1 = (overrides?.AddressLine1 ?? intent.AddressLine1 ?? string.Empty),
            AddressLine2 = overrides?.AddressLine2 ?? intent.AddressLine2,
            Suburb = overrides?.Suburb ?? intent.Suburb,
            City = overrides?.City ?? intent.City,
            Province = overrides?.Province ?? intent.Province,
            PostalCode = overrides?.PostalCode ?? intent.PostalCode,
            Country = overrides?.Country ?? intent.Country,
            Latitude = overrides?.Latitude ?? intent.Latitude,
            Longitude = overrides?.Longitude ?? intent.Longitude,
            GooglePlaceId = overrides?.GooglePlaceId ?? intent.GooglePlaceId,
            MapProviderReference = overrides?.MapProviderReference ?? intent.MapProviderReference,
            CustomerNotes = overrides?.CustomerNotes ?? intent.CustomerNotes,
            RequestedInstallationDateUtc = overrides?.RequestedInstallationDateUtc ?? intent.RequestedInstallationDateUtc,
            MockCheckoutPaymentProvider = overrides?.MockCheckoutPaymentProvider,
            MockCheckoutPaymentReference = overrides?.MockCheckoutPaymentReference,
        };
    }

    private async Task EnsureExpiredAsync(OrderIntent entity, CancellationToken cancellationToken)
    {
        if (entity.Status != OrderIntentStatus.Pending && entity.Status != OrderIntentStatus.Claimed)
            return;
        if (entity.ExpiresAtUtc > DateTime.UtcNow)
            return;

        entity.Status = OrderIntentStatus.Expired;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<ServicePackageDto?> ResolvePackageDtoAsync(Guid packageId, CancellationToken cancellationToken)
    {
        var package = await _dbContext.ServicePackages
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == packageId, cancellationToken);
        return MapPackageDto(package);
    }

    private static ServicePackageDto? MapPackageDto(ServicePackage? package)
    {
        if (package is null) return null;
        return new ServicePackageDto
        {
            Id = package.Id,
            Type = package.Type,
            Status = package.Status,
            Name = package.Name,
            Description = package.Description,
            ShortDescription = package.ShortDescription,
            SpeedLabel = package.SpeedLabel,
            DownloadSpeedMbps = package.DownloadSpeedMbps,
            UploadSpeedMbps = package.UploadSpeedMbps,
            DataAllowanceLabel = package.DataAllowanceLabel,
            IsUncapped = package.IsUncapped,
            Price = package.Price,
            BillingCycle = package.BillingCycle,
            ContractMonths = package.ContractMonths,
            HasFreeInstallation = package.HasFreeInstallation,
            InstallationFee = package.InstallationFee,
            IncludesRouter = package.IncludesRouter,
            RouterDescription = package.RouterDescription,
            IsFeatured = package.IsFeatured,
            DisplayOrder = package.DisplayOrder,
            TermsSummary = package.TermsSummary,
            CoverageNotes = package.CoverageNotes,
            ExternalReference = package.ExternalReference,
            CreatedAtUtc = package.CreatedAtUtc,
            UpdatedAtUtc = package.UpdatedAtUtc,
        };
    }

    private static OrderIntentDto MapToDto(OrderIntent entity, ServicePackageDto? package) => new()
    {
        Id = entity.Id,
        IntentToken = entity.IntentToken,
        ServicePackageId = entity.ServicePackageId,
        Package = package,
        FullName = entity.FullName,
        Email = entity.Email,
        PhoneNumber = entity.PhoneNumber,
        AddressLine1 = entity.AddressLine1,
        AddressLine2 = entity.AddressLine2,
        Suburb = entity.Suburb,
        City = entity.City,
        Province = entity.Province,
        PostalCode = entity.PostalCode,
        Country = entity.Country,
        Latitude = entity.Latitude,
        Longitude = entity.Longitude,
        GooglePlaceId = entity.GooglePlaceId,
        MapProviderReference = entity.MapProviderReference,
        RequestedInstallationDateUtc = entity.RequestedInstallationDateUtc,
        CustomerNotes = entity.CustomerNotes,
        Status = entity.Status,
        ClaimedByUserId = entity.ClaimedByUserId,
        ConvertedOrderId = entity.ConvertedOrderId,
        ExpiresAtUtc = entity.ExpiresAtUtc,
        CreatedAtUtc = entity.CreatedAtUtc,
        ClaimedAtUtc = entity.ClaimedAtUtc,
        ConvertedAtUtc = entity.ConvertedAtUtc,
    };

    private static Result<OrderIntentDto>? ValidatePublicFields(CreateOrderIntentRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.AddressLine1))
            return Result<OrderIntentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Address line 1 is required.");

        if (string.IsNullOrWhiteSpace(request.Suburb) && string.IsNullOrWhiteSpace(request.City))
            return Result<OrderIntentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Suburb or city is required.");

        if (string.IsNullOrWhiteSpace(request.FullName))
            return Result<OrderIntentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Full name is required.");

        if (string.IsNullOrWhiteSpace(request.Email))
            return Result<OrderIntentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Email is required so we can match the order to your account.");

        if (!EmailRegex.IsMatch(request.Email))
            return Result<OrderIntentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Email address is not a valid format.");

        if (string.IsNullOrWhiteSpace(request.PhoneNumber))
            return Result<OrderIntentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Phone number is required.");

        return null;
    }

    // 32 bytes → 256 bits of entropy, base64-url encoded for safe use in
    // a URL query string. URL-safe alphabet means no Uri.EscapeDataString
    // needed on either side of the redirect.
    private static string GenerateIntentToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? TruncateSource(string? source)
    {
        var trimmed = Trim(source);
        if (trimmed is null) return null;
        return trimmed.Length <= 50 ? trimmed : trimmed[..50];
    }
}

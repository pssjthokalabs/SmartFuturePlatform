using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auth;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.OrderIntents.Dtos;
using SmartFuture.Application.Orders;
using SmartFuture.Application.Orders.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Application.ServicePackages.Dtos;
using SmartFuture.Application.Users;
using SmartFuture.Domain.Customers;
using SmartFuture.Domain.Identity;
using SmartFuture.Domain.OrderIntents;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Auth;
using SmartFuture.Shared.Enums.Identity;
using SmartFuture.Shared.Enums.OrderIntents;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;
using SmartFuture.Shared.Utilities;

namespace SmartFuture.Application.OrderIntents;

// Public pre-order intent service. Owns the lifecycle of the
// OrderIntent entity (Pending → Claimed → ConvertedToOrder) and
// delegates real order creation back to IOrderService so backend
// pricing/audit/notifications stay in one place.
public partial class OrderIntentService : IOrderIntentService
{
    // 24h is short enough that abandoned intents don't accumulate
    // forever, but long enough that a visitor can leave the site, sleep
    // on it, and come back the next day. Adjust via configuration if
    // that becomes necessary.
    private static readonly TimeSpan IntentLifetime = TimeSpan.FromHours(72);

    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Handoff token lifetime. The website redirects the user immediately
    // after creating the account, so 10 minutes is plenty for a real
    // browser hop; long enough to forgive a slow handover, short enough
    // that a leaked token isn't useful for long.
    private static readonly TimeSpan HandoffTokenLifetime = TimeSpan.FromMinutes(10);

    private readonly IAppDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IOrderService _orderService;
    private readonly UserManager<User> _userManager;
    private readonly IPortalAuthHandoffService _handoffService;
    private readonly ILogger<OrderIntentService> _logger;

    // Phase 53 — "Order and Pay" client checkout dependencies.
    private readonly Payments.Paystack.IPaystackIntentInitiationService _paystackIntentInit;
    private readonly Payments.PayFast.IPayFastIntentInitiationService _payFastIntentInit;
    private readonly Payments.Ozow.IOzowIntentInitiationService _ozowIntentInit;
    private readonly Payments.Ozow.OzowSettings _ozowSettings53;
    private readonly Payments.IPaymentApplierService _paymentApplier;
    private readonly Payments.Mandates.ICustomerPaymentMandateService _mandates;
    private readonly Microsoft.Extensions.Hosting.IHostEnvironment _env53;
    private readonly Payments.PayFast.PayFastSettings _payFastSettings53;
    private readonly Payments.Paystack.PaystackSettings _paystackSettings53;

    // Billing engine — pro-rata + billing day picker.
    private readonly Billing.IBillingDayOptionService _billingDayOptions;
    private readonly Billing.BillingSettings _billingSettings;

    public OrderIntentService(IAppDbContext dbContext, ICurrentUserService currentUser, IOrderService orderService, UserManager<User> userManager,
        IPortalAuthHandoffService handoffService, ILogger<OrderIntentService> logger,
        Payments.Paystack.IPaystackIntentInitiationService paystackIntentInit,
        Payments.PayFast.IPayFastIntentInitiationService payFastIntentInit,
        Payments.Ozow.IOzowIntentInitiationService ozowIntentInit,
        Payments.IPaymentApplierService paymentApplier,
        Payments.Mandates.ICustomerPaymentMandateService mandates,
        Microsoft.Extensions.Hosting.IHostEnvironment env,
        Microsoft.Extensions.Options.IOptions<Payments.PayFast.PayFastSettings> payFastSettings,
        Microsoft.Extensions.Options.IOptions<Payments.Paystack.PaystackSettings> paystackSettings,
        Microsoft.Extensions.Options.IOptions<Payments.Ozow.OzowSettings> ozowSettings,
        Billing.IBillingDayOptionService billingDayOptions,
        Microsoft.Extensions.Options.IOptions<Billing.BillingSettings> billingSettings)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _orderService = orderService;
        _userManager = userManager;
        _handoffService = handoffService;
        _logger = logger;
        _paystackIntentInit = paystackIntentInit;
        _payFastIntentInit = payFastIntentInit;
        _ozowIntentInit = ozowIntentInit;
        _ozowSettings53 = ozowSettings.Value;
        _paymentApplier = paymentApplier;
        _mandates = mandates;
        _env53 = env;
        _payFastSettings53 = payFastSettings.Value;
        _paystackSettings53 = paystackSettings.Value;
        _billingDayOptions = billingDayOptions;
        _billingSettings = billingSettings.Value;
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

            // Optional selected variant carried from the website — validate
            // it belongs to the package + is active, then preserve it on the
            // intent so a ClientZone continuation keeps the customer's pick.
            Guid? variantId = null;
            if (request.ServicePackageVariantId is Guid vid && vid != Guid.Empty)
            {
                var variantOk = await _dbContext.ServicePackageVariants
                    .AsNoTracking()
                    .AnyAsync(v => v.Id == vid && v.ServicePackageId == package.Id && v.IsActive, cancellationToken);
                if (!variantOk)
                    return Result<OrderIntentDto>.Failure(
                        ErrorCodes.VALIDATION_ERROR, "The selected option is not valid for this package.");
                variantId = vid;
            }

            // Phase 50C — public acquisition flow rejects duplicate contact
            // details. If the visitor's email or phone already maps to an
            // account we send them to sign in instead of creating a
            // stranded OrderIntent they could never claim (claim is
            // bound to the user that the intent was created for via
            // the email match in the portal preview path; a duplicate
            // contact almost always means "I already have an account
            // and forgot"). Mirrors the registration duplicate checks
            // in AuthService.RegisterAsync (Phase 43 normalizer).
            var dup = await CheckDuplicateContactAsync(request.Email, request.PhoneNumber, cancellationToken);
            if (dup is not null) return dup;

            var now = DateTime.UtcNow;
            var entity = new OrderIntent
            {
                IntentToken = GenerateIntentToken(),
                ServicePackageId = package.Id,
                ServicePackageVariantId = variantId,
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

    public async Task<Result<OrderIntentWithRegistrationResponseDto>> RegisterAndCreateIntentAsync(CreateOrderIntentWithRegistrationRequestDto request, CancellationToken cancellationToken = default)
    {
        // Phase 50D — atomic register + intent. We need a real DB
        // transaction because UserManager.CreateAsync calls SaveChanges
        // internally and we'd otherwise leak a half-built account if the
        // intent insert later fails. The retrying execution strategy
        // forbids ambient user transactions outside ExecuteAsync, so
        // every write below runs inside the lambda.
        try
        {
            if (request is null)
                return Result<OrderIntentWithRegistrationResponseDto>.Failure(
                    ErrorCodes.BAD_REQUEST, "Request body is required.");

            var preflight = ValidateRegistrationFields(request);
            if (preflight is not null) return preflight;

            if (request.ServicePackageId == Guid.Empty)
                return Result<OrderIntentWithRegistrationResponseDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "ServicePackageId is required.");

            // Read the package outside the transaction — it's read-only,
            // and failing fast saves us from opening a user-creation txn
            // for an order we couldn't fulfil anyway.
            var package = await _dbContext.ServicePackages
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == request.ServicePackageId, cancellationToken);

            if (package is null)
                return Result<OrderIntentWithRegistrationResponseDto>.Failure(
                    ErrorCodes.NOT_FOUND, "The selected package is no longer available. Please pick another package.");

            if (package.Status != ServicePackageStatus.Active)
                return Result<OrderIntentWithRegistrationResponseDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "The selected package is not currently available for new orders.");

            // Duplicate gate — same logic the bare CreatePublicAsync uses,
            // so the website can rely on identical error codes.
            var dup = await CheckDuplicateContactForRegistrationAsync(request.Email, request.PhoneNumber, cancellationToken);
            if (dup is not null) return dup;

            // Pre-build the entities; UserManager owns the actual insert.
            var phoneRaw = Trim(request.PhoneNumber);
            var phoneNormalized = PhoneNumberNormalizer.Normalize(phoneRaw);
            var fullName = (request.FullName ?? string.Empty).Trim();
            var (firstName, lastName) = SplitName(fullName);

            // Allocate the user-number outside the txn — the allocator
            // does its own short transaction to avoid sequence gaps.
            var userNumber = await UserNumberAllocator.AllocateNextAsync(_dbContext);

            User? createdUser = null;
            OrderIntent? createdIntent = null;

            var strategy = _dbContext.CreateExecutionStrategy();
            var execResult = await strategy.ExecuteAsync<Result<OrderIntentWithRegistrationResponseDto>>(async () =>
            {
                await using var tx = await _dbContext.BeginTransactionAsync(cancellationToken);

                var user = new User
                {
                    UserName = request.Email.Trim(),
                    Email = request.Email.Trim(),
                    PhoneNumber = phoneRaw,
                    PhoneNumberNormalized = phoneNormalized,
                    FirstName = firstName,
                    LastName = lastName,
                    AccountStatus = UserAccountStatus.Active,
                    IsActive = true,
                    CreatedAtUtc = DateTime.UtcNow,
                    UserNumber = userNumber,
                };

                var identityResult = await _userManager.CreateAsync(user, request.Password);
                if (!identityResult.Succeeded)
                {
                    await tx.RollbackAsync(cancellationToken);
                    var message = string.Join("; ", identityResult.Errors.Select(e => e.Description));
                    var code = identityResult.Errors.Any(e =>
                        e.Code.Contains("Password", StringComparison.OrdinalIgnoreCase))
                        ? ErrorCodes.WEAK_PASSWORD
                        : ErrorCodes.VALIDATION_ERROR;
                    return Result<OrderIntentWithRegistrationResponseDto>.Failure(code, message);
                }

                var roleResult = await _userManager.AddToRoleAsync(user, SystemRoles.Customer);
                if (!roleResult.Succeeded)
                {
                    // Match AuthService.RegisterAsync: log + continue.
                    // The customer can still sign in; admin can fix the
                    // role assignment later.
                    _logger.LogWarning(
                        "User {UserId} created via website register but role assignment failed: {Errors}",
                        user.Id, string.Join("; ", roleResult.Errors.Select(e => e.Description)));
                }

                var hasProfile = await _dbContext.CustomerProfiles
                    .AnyAsync(p => p.UserId == user.Id, cancellationToken);
                if (!hasProfile)
                {
                    _dbContext.CustomerProfiles.Add(new CustomerProfile
                    {
                        UserId = user.Id,
                        AddressLine1 = Trim(request.AddressLine1),
                        Suburb = Trim(request.Suburb),
                        City = Trim(request.City),
                        Province = Trim(request.Province),
                        PostalCode = Trim(request.PostalCode),
                    });
                }

                // Validate the optional selected variant belongs to the
                // package + is active. Silently drop an invalid/stale id —
                // lead capture shouldn't hard-fail the whole registration.
                Guid? regVariantId = null;
                if (request.ServicePackageVariantId is Guid rvid && rvid != Guid.Empty)
                {
                    var variantOk = await _dbContext.ServicePackageVariants
                        .AnyAsync(v => v.Id == rvid && v.ServicePackageId == package.Id && v.IsActive, cancellationToken);
                    if (variantOk) regVariantId = rvid;
                }

                // Create the OrderIntent *already claimed* by the new
                // user — the next portal step is convert, not claim, so
                // a Pending → Claimed flip would be pointless work.
                var nowUtc = DateTime.UtcNow;
                var intent = new OrderIntent
                {
                    IntentToken = GenerateIntentToken(),
                    ServicePackageId = package.Id,
                    ServicePackageVariantId = regVariantId,
                    FullName = fullName.Length > 0 ? fullName : null,
                    Email = Trim(request.Email),
                    PhoneNumber = phoneRaw,
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
                    Status = OrderIntentStatus.Claimed,
                    ClaimedByUserId = user.Id,
                    ClaimedAtUtc = nowUtc,
                    ExpiresAtUtc = nowUtc.Add(IntentLifetime),
                    Source = TruncateSource(request.Source) ?? "Website",
                    // Phase 9 — record consent versions + timestamps
                    // alongside the intent. Validation upstream already
                    // guarantees AcceptedTerms / PrivacyAcknowledged are
                    // true here, so the timestamp is always set.
                    TermsVersion = TruncateVersion(request.TermsVersion),
                    TermsAcceptedAtUtc = nowUtc,
                    PrivacyVersion = TruncateVersion(request.PrivacyVersion),
                    PrivacyAcknowledgedAtUtc = nowUtc,
                };

                _dbContext.OrderIntents.Add(intent);
                await _dbContext.SaveChangesAsync(cancellationToken);

                // Issue the handoff token *inside* the same txn so a
                // failure here also unwinds the user + intent.
                var issued = await _handoffService.IssueAsync(
                    user.Id, intent.IntentToken, PortalAuthHandoffPurpose.WebsiteRegistration,
                    HandoffTokenLifetime, cancellationToken);

                await tx.CommitAsync(cancellationToken);

                createdUser = user;
                createdIntent = intent;

                return Result<OrderIntentWithRegistrationResponseDto>.Success(
                    new OrderIntentWithRegistrationResponseDto
                    {
                        IntentToken = intent.IntentToken,
                        HandoffToken = issued.RawToken,
                        IntentExpiresAtUtc = intent.ExpiresAtUtc,
                        HandoffExpiresAtUtc = issued.ExpiresAtUtc,
                        PortalHandoffPath =
                            $"/client/auth/handoff?token={Uri.EscapeDataString(issued.RawToken)}" +
                            $"&intentToken={Uri.EscapeDataString(intent.IntentToken)}",
                    },
                    "Account created and order intent saved.");
            });

            if (execResult.IsSuccess && createdUser is not null && createdIntent is not null)
            {
                _logger.LogInformation(
                    "Website registration created user {UserId} and OrderIntent {IntentId} for package {PackageId}.",
                    createdUser.Id, createdIntent.Id, createdIntent.ServicePackageId);
            }

            return execResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during website registration + intent creation.");
            return Result<OrderIntentWithRegistrationResponseDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred. Please try again.");
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

    // Phase 50C — duplicate-contact gate for the public wizard. Email
    // is checked case-insensitively against AspNetUsers.NormalizedEmail
    // (the column ASP.NET Identity already keeps populated). Phone is
    // compared in normalized "+27…" form so 0xx / 27xx / +27xx all
    // collide. Returns null when both are free; otherwise the friendly
    // error result the controller surfaces to the website.
    private async Task<Result<OrderIntentDto>?> CheckDuplicateContactAsync(string? email, string? phone, CancellationToken cancellationToken)
    {
        var emailTrimmed = Trim(email);
        if (!string.IsNullOrWhiteSpace(emailTrimmed))
        {
            var normalizedEmail = emailTrimmed.ToUpperInvariant();
            var emailTaken = await _dbContext.Users
                .AnyAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);
            if (emailTaken)
                return Result<OrderIntentDto>.Failure(
                    ErrorCodes.EMAIL_TAKEN,
                    "This email is already registered. Please sign in to continue.");
        }

        var phoneNormalized = PhoneNumberNormalizer.Normalize(Trim(phone));
        if (phoneNormalized is not null)
        {
            var phoneTaken = await _dbContext.Users
                .AnyAsync(u => u.PhoneNumberNormalized == phoneNormalized, cancellationToken);
            if (phoneTaken)
                return Result<OrderIntentDto>.Failure(
                    ErrorCodes.PHONE_TAKEN,
                    "This phone number is already registered. Please sign in to continue.");
        }

        return null;
    }

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
            PropertyType = overrides?.PropertyType ?? intent.PropertyType,
            BuildingComplexName = overrides?.BuildingComplexName ?? intent.BuildingComplexName,
            UnitNumber = overrides?.UnitNumber ?? intent.UnitNumber,
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
        ServicePackageVariantId = entity.ServicePackageVariantId,
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
        PropertyType = entity.PropertyType,
        BuildingComplexName = entity.BuildingComplexName,
        UnitNumber = entity.UnitNumber,
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

    private static Result<OrderIntentWithRegistrationResponseDto>? ValidateRegistrationFields(CreateOrderIntentWithRegistrationRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.AddressLine1))
            return Result<OrderIntentWithRegistrationResponseDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "Address line 1 is required.");
        if (string.IsNullOrWhiteSpace(request.Suburb) && string.IsNullOrWhiteSpace(request.City))
            return Result<OrderIntentWithRegistrationResponseDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "Suburb or city is required.");
        if (string.IsNullOrWhiteSpace(request.FullName))
            return Result<OrderIntentWithRegistrationResponseDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "Full name is required.");
        if (string.IsNullOrWhiteSpace(request.Email))
            return Result<OrderIntentWithRegistrationResponseDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "Email is required.");
        if (!EmailRegex.IsMatch(request.Email))
            return Result<OrderIntentWithRegistrationResponseDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "Email address is not a valid format.");
        if (string.IsNullOrWhiteSpace(request.PhoneNumber))
            return Result<OrderIntentWithRegistrationResponseDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "Phone number is required.");
        if (string.IsNullOrWhiteSpace(request.Password))
            return Result<OrderIntentWithRegistrationResponseDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "Password is required.");
        if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
            return Result<OrderIntentWithRegistrationResponseDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "Passwords do not match.");
        // Phase 9 — legal consent must be ticked before we create the
        // user. Single checkbox on the wizard sets both flags; the
        // backend still checks them independently so a future split
        // into two checkboxes doesn't silently lose a consent.
        if (!request.AcceptedTerms)
            return Result<OrderIntentWithRegistrationResponseDto>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                "Please accept the Terms of Use to continue.");
        if (!request.PrivacyAcknowledged)
            return Result<OrderIntentWithRegistrationResponseDto>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                "Please acknowledge the Privacy Policy to continue.");
        return null;
    }

    private async Task<Result<OrderIntentWithRegistrationResponseDto>?> CheckDuplicateContactForRegistrationAsync(string? email, string? phone, CancellationToken cancellationToken)
    {
        var emailTrimmed = Trim(email);
        if (!string.IsNullOrWhiteSpace(emailTrimmed))
        {
            var normalizedEmail = emailTrimmed.ToUpperInvariant();
            var emailTaken = await _dbContext.Users
                .AnyAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);
            if (emailTaken)
                return Result<OrderIntentWithRegistrationResponseDto>.Failure(
                    ErrorCodes.EMAIL_TAKEN,
                    "This email is already registered. Please sign in to continue.");
        }

        var phoneNormalized = PhoneNumberNormalizer.Normalize(Trim(phone));
        if (phoneNormalized is not null)
        {
            var phoneTaken = await _dbContext.Users
                .AnyAsync(u => u.PhoneNumberNormalized == phoneNormalized, cancellationToken);
            if (phoneTaken)
                return Result<OrderIntentWithRegistrationResponseDto>.Failure(
                    ErrorCodes.PHONE_TAKEN,
                    "This phone number is already registered. Please sign in to continue.");
        }

        return null;
    }

    // First token = FirstName, remainder = LastName. We accept whatever
    // shape the visitor typed; if there's only one word it goes into
    // FirstName and LastName stays blank — registration validation only
    // requires "FullName" to be non-empty so this is intentional.
    private static (string FirstName, string LastName) SplitName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return (string.Empty, string.Empty);
        var parts = fullName.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => (string.Empty, string.Empty),
            1 => (parts[0], string.Empty),
            _ => (parts[0], parts[1]),
        };
    }

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

    // Phase 9 — defensive cap on legal version strings. Schema reserves
    // 20 chars; anything longer is the website misbehaving and we'd
    // rather truncate than reject the consent.
    private static string? TruncateVersion(string? version)
    {
        var trimmed = Trim(version);
        if (trimmed is null) return null;
        return trimmed.Length <= 20 ? trimmed : trimmed[..20];
    }
}

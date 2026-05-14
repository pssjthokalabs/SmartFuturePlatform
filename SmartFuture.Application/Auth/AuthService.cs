using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Auth.Dtos;
using SmartFuture.Application.Common.Interfaces.Identity;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Customers;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Identity;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Auth;

public class AuthService : IAuthService
{
    private readonly UserManager<User> _userManager;
    private readonly SignInManager<User> _signInManager;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;
    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<AuthService> _logger;

    public AuthService(UserManager<User> userManager, SignInManager<User> signInManager, IJwtTokenGenerator jwtTokenGenerator, IAppDbContext dbContext, IAuditService auditService,
        ICurrentUserService currentUser, ILogger<AuthService> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _jwtTokenGenerator = jwtTokenGenerator;
        _dbContext = dbContext;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<Result<AuthTokenDto>> RegisterAsync(RegisterRequestDto request)
    {
        try
        {
            if (request is null)
                return Result<AuthTokenDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            if (string.IsNullOrWhiteSpace(request.FirstName)
                || string.IsNullOrWhiteSpace(request.LastName)
                || string.IsNullOrWhiteSpace(request.Email)
                || string.IsNullOrWhiteSpace(request.Password))
            {
                return Result<AuthTokenDto>.Failure(ErrorCodes.VALIDATION_ERROR,
                    "FirstName, LastName, Email and Password are required.");
            }

            if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
                return Result<AuthTokenDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Passwords do not match.");

            var existing = await _userManager.FindByEmailAsync(request.Email);
            if (existing is not null)
                return Result<AuthTokenDto>.Failure(ErrorCodes.EMAIL_TAKEN, "Email is already in use.");

            var user = new User
            {
                UserName = request.Email,
                Email = request.Email,
                PhoneNumber = string.IsNullOrWhiteSpace(request.PhoneNumber) ? null : request.PhoneNumber,
                FirstName = request.FirstName.Trim(),
                LastName = request.LastName.Trim(),
                AccountStatus = UserAccountStatus.Active,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow
            };

            var createResult = await _userManager.CreateAsync(user, request.Password);
            if (!createResult.Succeeded)
            {
                var message = string.Join("; ", createResult.Errors.Select(e => e.Description));
                var code = createResult.Errors.Any(e =>
                    e.Code.Contains("Password", StringComparison.OrdinalIgnoreCase))
                    ? ErrorCodes.WEAK_PASSWORD
                    : ErrorCodes.VALIDATION_ERROR;

                return Result<AuthTokenDto>.Failure(code, message);
            }

            var roleResult = await _userManager.AddToRoleAsync(user, SystemRoles.Customer);
            if (!roleResult.Succeeded)
            {
                _logger.LogWarning("User {UserId} created but role assignment failed: {Errors}",
                    user.Id, string.Join("; ", roleResult.Errors.Select(e => e.Description)));
            }

            var hasProfile = await _dbContext.CustomerProfiles.AnyAsync(p => p.UserId == user.Id);
            if (!hasProfile)
            {
                _dbContext.CustomerProfiles.Add(new CustomerProfile
                {
                    UserId = user.Id
                });
                await _dbContext.SaveChangesAsync();
            }

            var token = await _jwtTokenGenerator.GenerateTokenAsync(user);

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = user.Id,
                ActorType = AuditActorType.User,
                ActionType = AuditActionType.UserRegistered,
                EntityType = AuditEntityType.User,
                EntityId = user.Id,
                EntityName = user.Email,
                Summary = $"User registered: {user.Email}",
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });

            return Result<AuthTokenDto>.Success(token, "Registration successful.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during registration for {Email}", request?.Email);
            return Result<AuthTokenDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred during registration.");
        }
    }

    public async Task<Result<AuthTokenDto>> LoginAsync(LoginRequestDto request)
    {
        try
        {
            if (request is null || string.IsNullOrWhiteSpace(request.EmailOrPhone) || string.IsNullOrWhiteSpace(request.Password))
                return Result<AuthTokenDto>.Failure(ErrorCodes.VALIDATION_ERROR, "EmailOrPhone and Password are required.");

            var user = await _userManager.FindByEmailAsync(request.EmailOrPhone);
            if (user is null)
            {
                user = await _userManager.Users
                    .FirstOrDefaultAsync(u => u.PhoneNumber == request.EmailOrPhone);
            }

            if (user is null)
                return Result<AuthTokenDto>.Failure(ErrorCodes.INVALID_CREDENTIALS, "Invalid credentials.");

            if (!user.IsActive || user.AccountStatus == UserAccountStatus.Suspended || user.AccountStatus == UserAccountStatus.Inactive)
                return Result<AuthTokenDto>.Failure(ErrorCodes.FORBIDDEN, "Account is not allowed to sign in.");

            var passwordCheck = await _signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: false);
            if (!passwordCheck.Succeeded)
                return Result<AuthTokenDto>.Failure(ErrorCodes.INVALID_CREDENTIALS, "Invalid credentials.");

            var token = await _jwtTokenGenerator.GenerateTokenAsync(user);

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = user.Id,
                ActorType = AuditActorType.User,
                ActionType = AuditActionType.UserLoggedIn,
                EntityType = AuditEntityType.Auth,
                EntityId = user.Id,
                EntityName = user.Email,
                Summary = $"User logged in: {user.Email}",
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });

            return Result<AuthTokenDto>.Success(token, "Login successful.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during login for {Identifier}", request?.EmailOrPhone);
            return Result<AuthTokenDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred during login.");
        }
    }

    public async Task<Result<AuthTokenDto>> RefreshTokenAsync(RefreshTokenRequestDto request)
    {
        try
        {
            if (request is null || string.IsNullOrWhiteSpace(request.RefreshToken))
                return Result<AuthTokenDto>.Failure(ErrorCodes.VALIDATION_ERROR, "RefreshToken is required.");

            var token = await _jwtTokenGenerator.RefreshTokenAsync(request.RefreshToken);
            if (token is null)
                return Result<AuthTokenDto>.Failure(ErrorCodes.INVALID_REFRESH_TOKEN, "Refresh token is invalid or expired.");

            return Result<AuthTokenDto>.Success(token, "Token refreshed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during refresh-token rotation");
            return Result<AuthTokenDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred during refresh.");
        }
    }

    public async Task<Result> RevokeRefreshTokenAsync(RefreshTokenRequestDto request)
    {
        try
        {
            if (request is null || string.IsNullOrWhiteSpace(request.RefreshToken))
                return Result.Failure(ErrorCodes.VALIDATION_ERROR, "RefreshToken is required.");

            var revoked = await _jwtTokenGenerator.RevokeRefreshTokenAsync(request.RefreshToken);

            if (revoked)
            {
                await _auditService.LogAsync(new CreateAuditLogRequestDto
                {
                    ActorUserId = _currentUser.UserId,
                    ActorType = _currentUser.UserId.HasValue ? AuditActorType.User : AuditActorType.System,
                    ActionType = AuditActionType.RefreshTokenRevoked,
                    EntityType = AuditEntityType.Auth,
                    EntityId = _currentUser.UserId,
                    Summary = "Refresh token revoked",
                    IpAddress = _currentUser.IpAddress,
                    UserAgent = _currentUser.UserAgent,
                    IsSuccess = true
                });
            }

            return revoked
                ? Result.Success("Refresh token revoked.")
                : Result.Failure(ErrorCodes.INVALID_REFRESH_TOKEN, "Refresh token not found or already revoked.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during refresh-token revocation");
            return Result.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred during revoke.");
        }
    }

    public async Task<Result<CurrentUserDto>> GetCurrentUserAsync(Guid userId)
    {
        try
        {
            if (userId == Guid.Empty)
                return Result<CurrentUserDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user is null)
                return Result<CurrentUserDto>.Failure(ErrorCodes.NOT_FOUND, "User not found.");

            var roles = await _userManager.GetRolesAsync(user);

            var dto = new CurrentUserDto
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email ?? string.Empty,
                PhoneNumber = user.PhoneNumber,
                AccountStatus = user.AccountStatus.ToString(),
                IsActive = user.IsActive,
                Roles = roles.ToList()
            };

            return Result<CurrentUserDto>.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching current user {UserId}", userId);
            return Result<CurrentUserDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred fetching user.");
        }
    }
}

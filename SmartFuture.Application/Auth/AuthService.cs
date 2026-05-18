using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Auth.Dtos;
using SmartFuture.Application.Common.Interfaces.Identity;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Customers;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Identity;
using SmartFuture.Shared.Enums.Notifications;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Auth;

public class AuthService : IAuthService
{
    // Generic safe message returned by forgot-password regardless of whether
    // the email matched a real account. Prevents account-enumeration via
    // probing this endpoint.
    private const string ForgotPasswordSafeMessage =
        "If the email is registered, password reset instructions will be sent.";

    private readonly UserManager<User> _userManager;
    private readonly SignInManager<User> _signInManager;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;
    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly INotificationService _notifications;
    private readonly ICurrentUserService _currentUser;
    private readonly FrontendSettings _frontendSettings;
    private readonly ILogger<AuthService> _logger;

    public AuthService(UserManager<User> userManager, SignInManager<User> signInManager, IJwtTokenGenerator jwtTokenGenerator, IAppDbContext dbContext, IAuditService auditService,
        INotificationService notifications, ICurrentUserService currentUser, IOptions<FrontendSettings> frontendSettings, ILogger<AuthService> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _jwtTokenGenerator = jwtTokenGenerator;
        _dbContext = dbContext;
        _auditService = auditService;
        _notifications = notifications;
        _currentUser = currentUser;
        _frontendSettings = frontendSettings.Value;
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
                    UserId = user.Id,
                    AddressLine1 = NullIfBlank(request.AddressLine1),
                    Suburb       = NullIfBlank(request.Suburb),
                    City         = NullIfBlank(request.City),
                    Province     = NullIfBlank(request.Province),
                    PostalCode   = NullIfBlank(request.PostalCode)
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

    public async Task<Result> ForgotPasswordAsync(ForgotPasswordRequestDto request)
    {
        // Forgot-password is intentionally non-discriminating: regardless of
        // whether the email exists, we return the same generic success. This
        // prevents using the endpoint as an account-enumeration oracle.
        // The only failures surfaced are server-side / configuration faults.
        try
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Email))
                return Result.Failure(ErrorCodes.VALIDATION_ERROR, "Email is required.");

            var email = request.Email.Trim();
            var user = await _userManager.FindByEmailAsync(email);

            // Account-not-found is a no-op on the surface; we still log a
            // server-side audit entry so abuse patterns are visible.
            if (user is null)
            {
                await _auditService.LogAsync(new CreateAuditLogRequestDto
                {
                    ActorUserId = null,
                    ActorType = AuditActorType.System,
                    ActionType = AuditActionType.PasswordResetRequested,
                    EntityType = AuditEntityType.Auth,
                    EntityId = null,
                    EntityName = email,
                    Summary = $"Password reset requested for unknown email: {email}",
                    IpAddress = _currentUser.IpAddress,
                    UserAgent = _currentUser.UserAgent,
                    IsSuccess = false
                });
                return Result.Success(ForgotPasswordSafeMessage);
            }

            if (!user.IsActive || user.AccountStatus == UserAccountStatus.Suspended)
            {
                // Don't send a reset email to a disabled account; still return
                // the safe message so the caller can't tell.
                await _auditService.LogAsync(new CreateAuditLogRequestDto
                {
                    ActorUserId = user.Id,
                    ActorType = AuditActorType.User,
                    ActionType = AuditActionType.PasswordResetRequested,
                    EntityType = AuditEntityType.Auth,
                    EntityId = user.Id,
                    EntityName = user.Email,
                    Summary = $"Password reset suppressed for inactive/suspended account: {user.Email}",
                    IpAddress = _currentUser.IpAddress,
                    UserAgent = _currentUser.UserAgent,
                    IsSuccess = false
                });
                return Result.Success(ForgotPasswordSafeMessage);
            }

            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            // Tokens contain `=` / `+` / `/` characters that don't round-trip
            // through query strings unaltered. Base64Url-encode the raw bytes
            // so the link survives copy-paste from email clients.
            var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

            var portalKey = (request.Portal ?? "client").Trim().ToLowerInvariant();
            var baseUrl = portalKey == "admin"
                ? _frontendSettings.AdminResetPasswordUrl
                : _frontendSettings.ClientResetPasswordUrl;
            var separator = baseUrl.Contains('?') ? '&' : '?';
            var resetUrl =
                $"{baseUrl}{separator}email={Uri.EscapeDataString(user.Email ?? email)}&token={encodedToken}";

            var greetingName = string.IsNullOrWhiteSpace(user.FirstName) ? "there" : user.FirstName;
            var portalName = portalKey == "admin" ? "Smart Future admin portal" : "Smart Future account";
            var subject = "Smart Future Password Reset";
            var body =
                $"Hi {greetingName},\n\n" +
                $"We received a request to reset the password for your {portalName} ({user.Email}).\n\n" +
                $"Reset your password using the link below. For your security, this link will expire after a short time " +
                $"and can only be used once:\n\n" +
                $"{resetUrl}\n\n" +
                $"If you did not request a password reset, you can safely ignore this email — your password will not change.\n\n" +
                $"If you need help, reply to this email and our team will get back to you.\n\n" +
                $"— The Smart Future team";

            await _notifications.SendAsync(new SendNotificationRequestDto
            {
                UserId = user.Id,
                Channel = NotificationChannel.Email,
                Type = NotificationType.PasswordReset,
                RecipientEmail = user.Email,
                Subject = subject,
                Body = body,
                RelatedEntityType = "User",
                RelatedEntityId = user.Id
            });

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = user.Id,
                ActorType = AuditActorType.User,
                ActionType = AuditActionType.PasswordResetRequested,
                EntityType = AuditEntityType.Auth,
                EntityId = user.Id,
                EntityName = user.Email,
                Summary = $"Password reset email queued for {user.Email} (portal: {portalKey})",
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });

            return Result.Success(ForgotPasswordSafeMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during forgot-password for {Email}", request?.Email);
            // Still return safe message — surfacing exceptions here also leaks
            // information. The error is logged for ops to follow up.
            return Result.Success(ForgotPasswordSafeMessage);
        }
    }

    public async Task<Result> ResetPasswordAsync(ResetPasswordRequestDto request)
    {
        try
        {
            if (request is null
                || string.IsNullOrWhiteSpace(request.Email)
                || string.IsNullOrWhiteSpace(request.Token)
                || string.IsNullOrWhiteSpace(request.NewPassword))
            {
                return Result.Failure(ErrorCodes.VALIDATION_ERROR,
                    "Email, token, and new password are required.");
            }

            if (!string.Equals(request.NewPassword, request.ConfirmPassword, StringComparison.Ordinal))
                return Result.Failure(ErrorCodes.VALIDATION_ERROR, "Passwords do not match.");

            var user = await _userManager.FindByEmailAsync(request.Email.Trim());
            if (user is null)
            {
                // Friendly message that doesn't confirm or deny the email; the
                // most common cause of a missing user here is a stale link.
                return Result.Failure(ErrorCodes.UNAUTHORIZED,
                    "This reset link is no longer valid. Please request a new one.");
            }

            string decodedToken;
            try
            {
                decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(request.Token));
            }
            catch
            {
                return Result.Failure(ErrorCodes.UNAUTHORIZED,
                    "This reset link is invalid. Please request a new one.");
            }

            var resetResult = await _userManager.ResetPasswordAsync(user, decodedToken, request.NewPassword);
            if (!resetResult.Succeeded)
            {
                var message = string.Join("; ", resetResult.Errors.Select(e => e.Description));
                var hasPasswordError = resetResult.Errors.Any(e =>
                    e.Code.Contains("Password", StringComparison.OrdinalIgnoreCase));
                var hasTokenError = resetResult.Errors.Any(e =>
                    e.Code.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
                    e.Code.Contains("InvalidToken", StringComparison.OrdinalIgnoreCase));

                if (hasTokenError)
                {
                    return Result.Failure(ErrorCodes.UNAUTHORIZED,
                        "This reset link has expired or is invalid. Please request a new one.");
                }

                return Result.Failure(
                    hasPasswordError ? ErrorCodes.WEAK_PASSWORD : ErrorCodes.VALIDATION_ERROR,
                    string.IsNullOrWhiteSpace(message) ? "Could not reset password." : message);
            }

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = user.Id,
                ActorType = AuditActorType.User,
                ActionType = AuditActionType.PasswordResetCompleted,
                EntityType = AuditEntityType.Auth,
                EntityId = user.Id,
                EntityName = user.Email,
                Summary = $"Password reset completed for {user.Email}",
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });

            return Result.Success("Your password has been reset. You can now sign in.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during password reset for {Email}", request?.Email);
            return Result.Failure(ErrorCodes.EXCEPTION,
                "An unexpected error occurred while resetting your password.");
        }
    }

    private const int ChangePasswordCodeLength = 6;
    private const int ChangePasswordCodeTtlMinutes = 10;
    private const int ChangePasswordMaxAttempts = 5;

    public async Task<Result> RequestChangePasswordCodeAsync(Guid userId, RequestChangePasswordCodeRequestDto request)
    {
        try
        {
            if (userId == Guid.Empty)
                return Result.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user is null)
                return Result.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");
            if (!user.IsActive)
                return Result.Failure(ErrorCodes.FORBIDDEN, "Account is not active.");

            var channel = ParseChannel(request?.Channel);

            // SMS delivery is not wired to a real provider yet — the only
            // INotificationSender that exists is SMTP, with a logging
            // fallback for non-email channels. Faking SMS delivery would
            // mislead users into waiting for a text that never arrives,
            // so refuse the SMS path explicitly and let the UI fall back
            // to email.
            if (channel == VerificationCodeChannel.Sms)
            {
                await _auditService.LogAsync(new CreateAuditLogRequestDto
                {
                    ActorUserId = user.Id,
                    ActorType = AuditActorType.User,
                    ActionType = AuditActionType.PasswordChangeCodeRequested,
                    EntityType = AuditEntityType.Auth,
                    EntityId = user.Id,
                    EntityName = user.Email,
                    Summary = "Password-change SMS code requested, but SMS provider is not configured.",
                    IpAddress = _currentUser.IpAddress,
                    UserAgent = _currentUser.UserAgent,
                    IsSuccess = false
                });
                return Result.Failure(ErrorCodes.SMS_NOT_CONFIGURED,
                    "SMS delivery is not configured yet. Please use the email option.");
            }

            if (string.IsNullOrWhiteSpace(user.Email))
                return Result.Failure(ErrorCodes.VALIDATION_ERROR,
                    "Your account has no email address on file. Please contact support.");

            // Invalidate any previously-issued unconsumed codes for the
            // same purpose so older codes can't be replayed after a fresh
            // request. We mark them consumed rather than deleting to keep
            // the audit trail intact.
            var now = DateTime.UtcNow;
            var existing = await _dbContext.VerificationCodes
                .Where(c => c.UserId == user.Id
                    && c.Purpose == VerificationCodePurpose.ChangePassword
                    && c.ConsumedAtUtc == null)
                .ToListAsync();
            foreach (var c in existing)
                c.ConsumedAtUtc = now;

            var code = GenerateNumericCode(ChangePasswordCodeLength);
            var record = new VerificationCode
            {
                UserId = user.Id,
                Purpose = VerificationCodePurpose.ChangePassword,
                Channel = channel,
                CodeHash = HashCode(code),
                ExpiresAtUtc = now.AddMinutes(ChangePasswordCodeTtlMinutes),
                MaxAttempts = ChangePasswordMaxAttempts
            };
            _dbContext.VerificationCodes.Add(record);
            await _dbContext.SaveChangesAsync();

            var subject = "Smart Future password change code";
            // The full code goes in the body sent to the user only. We
            // deliberately never log the code itself.
            var body =
                $"Hi {(string.IsNullOrWhiteSpace(user.FirstName) ? "there" : user.FirstName)},\n\n" +
                $"Use the verification code below to change your Smart Future password.\n\n" +
                $"    {code}\n\n" +
                $"This code expires in {ChangePasswordCodeTtlMinutes} minutes and can only be used once. " +
                $"If you didn't request a password change, you can ignore this email — your password will not change.\n\n" +
                $"— The Smart Future team";

            await _notifications.SendAsync(new SendNotificationRequestDto
            {
                UserId = user.Id,
                Channel = NotificationChannel.Email,
                Type = NotificationType.PasswordReset,
                RecipientEmail = user.Email,
                Subject = subject,
                Body = body,
                RelatedEntityType = "User",
                RelatedEntityId = user.Id
            });

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = user.Id,
                ActorType = AuditActorType.User,
                ActionType = AuditActionType.PasswordChangeCodeRequested,
                EntityType = AuditEntityType.Auth,
                EntityId = user.Id,
                EntityName = user.Email,
                Summary = $"Password-change code queued via {channel} for {user.Email}",
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });

            return Result.Success("A verification code has been sent.");
        }
        catch (Exception ex)
        {
            // Don't include the code in the log even on error — keep
            // the failure message generic.
            _logger.LogError(ex, "Unexpected error requesting password-change code for {UserId}", userId);
            return Result.Failure(ErrorCodes.EXCEPTION,
                "An unexpected error occurred while requesting the code.");
        }
    }

    public async Task<Result> ConfirmChangePasswordAsync(Guid userId, ConfirmChangePasswordRequestDto request)
    {
        try
        {
            if (userId == Guid.Empty)
                return Result.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (request is null
                || string.IsNullOrWhiteSpace(request.Code)
                || string.IsNullOrWhiteSpace(request.NewPassword))
            {
                return Result.Failure(ErrorCodes.VALIDATION_ERROR,
                    "Code and new password are required.");
            }

            if (!string.Equals(request.NewPassword, request.ConfirmPassword, StringComparison.Ordinal))
                return Result.Failure(ErrorCodes.VALIDATION_ERROR, "Passwords do not match.");

            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user is null)
                return Result.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");
            if (!user.IsActive)
                return Result.Failure(ErrorCodes.FORBIDDEN, "Account is not active.");

            var now = DateTime.UtcNow;
            var record = await _dbContext.VerificationCodes
                .Where(c => c.UserId == user.Id
                    && c.Purpose == VerificationCodePurpose.ChangePassword
                    && c.ConsumedAtUtc == null)
                .OrderByDescending(c => c.CreatedAtUtc)
                .FirstOrDefaultAsync();

            if (record is null)
            {
                await LogChangePasswordFailure(user, "No active code on file.");
                return Result.Failure(ErrorCodes.VERIFICATION_CODE_INVALID,
                    "This code is invalid. Please request a new one.");
            }

            if (record.ExpiresAtUtc <= now)
            {
                record.ConsumedAtUtc = now;
                await _dbContext.SaveChangesAsync();
                await LogChangePasswordFailure(user, "Code expired.");
                return Result.Failure(ErrorCodes.VERIFICATION_CODE_EXPIRED,
                    "This code has expired. Please request a new one.");
            }

            if (record.AttemptCount >= record.MaxAttempts)
            {
                record.ConsumedAtUtc = now;
                await _dbContext.SaveChangesAsync();
                await LogChangePasswordFailure(user, "Maximum attempts exceeded.");
                return Result.Failure(ErrorCodes.VERIFICATION_CODE_ATTEMPTS_EXCEEDED,
                    "Too many attempts. Please request a new code.");
            }

            var providedHash = HashCode(request.Code.Trim());
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(record.CodeHash),
                    Encoding.ASCII.GetBytes(providedHash)))
            {
                record.AttemptCount += 1;
                record.LastAttemptAtUtc = now;
                await _dbContext.SaveChangesAsync();
                await LogChangePasswordFailure(user, "Code did not match.");
                return Result.Failure(ErrorCodes.VERIFICATION_CODE_INVALID,
                    "This code is invalid. Please check it and try again.");
            }

            // Code valid — change the password via Identity. Generate a
            // reset token first so we go through the standard password
            // validators (length, complexity, etc.).
            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var resetResult = await _userManager.ResetPasswordAsync(user, token, request.NewPassword);
            if (!resetResult.Succeeded)
            {
                var message = string.Join("; ", resetResult.Errors.Select(e => e.Description));
                var weak = resetResult.Errors.Any(e =>
                    e.Code.Contains("Password", StringComparison.OrdinalIgnoreCase));
                return Result.Failure(
                    weak ? ErrorCodes.WEAK_PASSWORD : ErrorCodes.VALIDATION_ERROR,
                    string.IsNullOrWhiteSpace(message) ? "Could not change password." : message);
            }

            record.ConsumedAtUtc = now;
            await _dbContext.SaveChangesAsync();

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = user.Id,
                ActorType = AuditActorType.User,
                ActionType = AuditActionType.PasswordChanged,
                EntityType = AuditEntityType.Auth,
                EntityId = user.Id,
                EntityName = user.Email,
                Summary = $"Password changed for {user.Email}",
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });

            return Result.Success("Your password has been updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error confirming password change for {UserId}", userId);
            return Result.Failure(ErrorCodes.EXCEPTION,
                "An unexpected error occurred while changing your password.");
        }
    }

    private async Task LogChangePasswordFailure(User user, string summary)
    {
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = user.Id,
            ActorType = AuditActorType.User,
            ActionType = AuditActionType.PasswordChangeCodeFailed,
            EntityType = AuditEntityType.Auth,
            EntityId = user.Id,
            EntityName = user.Email,
            Summary = summary,
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = false
        });
    }

    private static VerificationCodeChannel ParseChannel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return VerificationCodeChannel.Email;
        return value.Trim().ToLowerInvariant() switch
        {
            "sms" => VerificationCodeChannel.Sms,
            _ => VerificationCodeChannel.Email
        };
    }

    private static string GenerateNumericCode(int length)
    {
        // RNGCryptoServiceProvider-style secure RNG. We sample bytes
        // and reduce mod 10 — biased by 6 over a 256-byte field, which
        // is acceptable for a 6-digit short-lived OTP. (For longer
        // codes the bias would matter more; we'd switch to rejection
        // sampling.)
        var bytes = RandomNumberGenerator.GetBytes(length);
        var sb = new StringBuilder(length);
        foreach (var b in bytes) sb.Append((char)('0' + (b % 10)));
        return sb.ToString();
    }

    private static string HashCode(string code)
    {
        var bytes = Encoding.UTF8.GetBytes(code);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
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

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

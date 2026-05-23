using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Auth.Dtos;
using SmartFuture.Application.Common.Interfaces.Identity;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Communication.Email.Templates;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Application.Users;
using SmartFuture.Domain.Customers;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Identity;
using SmartFuture.Shared.Enums.Notifications;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;
using SmartFuture.Shared.Utilities;

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
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<AuthService> _logger;

    public AuthService(UserManager<User> userManager, SignInManager<User> signInManager, IJwtTokenGenerator jwtTokenGenerator, IAppDbContext dbContext, IAuditService auditService,
        INotificationService notifications, ICurrentUserService currentUser, IOptions<FrontendSettings> frontendSettings, IHostEnvironment hostEnvironment, ILogger<AuthService> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _jwtTokenGenerator = jwtTokenGenerator;
        _dbContext = dbContext;
        _auditService = auditService;
        _notifications = notifications;
        _currentUser = currentUser;
        _frontendSettings = frontendSettings.Value;
        _hostEnvironment = hostEnvironment;
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
                return Result<AuthTokenDto>.Failure(ErrorCodes.EMAIL_TAKEN, "This email address is already registered.");

            // Phase 43 — phone uniqueness. Compare in canonical form so
            // "0737942244", "27737942244", and "+27737942244" collide.
            var phoneRaw = NullIfBlank(request.PhoneNumber);
            var phoneNormalized = PhoneNumberNormalizer.Normalize(phoneRaw);
            if (phoneNormalized is not null)
            {
                var phoneClash = await _dbContext.Users
                    .AnyAsync(u => u.PhoneNumberNormalized == phoneNormalized);
                if (phoneClash)
                {
                    return Result<AuthTokenDto>.Failure(ErrorCodes.PHONE_TAKEN,
                        "This phone number is already registered. Please use a different number or sign in.");
                }
            }

            var user = new User
            {
                UserName    = request.Email,
                Email       = request.Email,
                PhoneNumber = phoneRaw,
                PhoneNumberNormalized = phoneNormalized,
                FirstName   = request.FirstName.Trim(),
                LastName    = request.LastName.Trim(),
                AccountStatus = UserAccountStatus.Active,
                IsActive    = true,
                CreatedAtUtc = DateTime.UtcNow,
                // Phase 41 — friendly user number for the admin portal.
                UserNumber  = await UserNumberAllocator.AllocateNextAsync(_dbContext)
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

            // Phase 35C — forgot-password is now a 6-digit OTP flow.
            // Reuses the VerificationCodes table (Purpose=PasswordReset),
            // matching the change-password OTP design. The reset link
            // path is gone; ResetPasswordAsync expects (email, code,
            // newPassword) and validates against this row.

            var now = DateTime.UtcNow;
            var portalKey = (request.Portal ?? "client").Trim().ToLowerInvariant();

            // Consume any outstanding password-reset codes for this user
            // so only one is valid at a time — stops a stale code being
            // used after the user re-requests a fresh one.
            var existing = await _dbContext.VerificationCodes
                .Where(c => c.UserId == user.Id
                    && c.Purpose == VerificationCodePurpose.PasswordReset
                    && c.ConsumedAtUtc == null)
                .ToListAsync();
            foreach (var c in existing) c.ConsumedAtUtc = now;

            var code = GenerateNumericCode(PasswordResetCodeLength);
            var record = new Domain.Identity.VerificationCode
            {
                UserId = user.Id,
                Purpose = VerificationCodePurpose.PasswordReset,
                Channel = VerificationCodeChannel.Email,
                CodeHash = HashCode(code),
                ExpiresAtUtc = now.AddMinutes(PasswordResetCodeTtlMinutes),
                MaxAttempts = PasswordResetMaxAttempts
            };
            _dbContext.VerificationCodes.Add(record);
            await _dbContext.SaveChangesAsync();

            // Template owns the body. The plaintext code only ever
            // reaches the rendered email — it is never logged.
            var template = AuthEmailTemplates.PasswordResetCode(
                firstName: user.FirstName ?? string.Empty,
                code: code,
                expiryMinutes: PasswordResetCodeTtlMinutes);

            await _notifications.SendAsync(new SendNotificationRequestDto
            {
                UserId = user.Id,
                Channel = NotificationChannel.Email,
                Type = NotificationType.PasswordReset,
                RecipientEmail = user.Email,
                Subject = template.Subject,
                Body = template.PlainTextBody,
                IsHtml = true,
                HtmlBody = template.HtmlBody,
                SenderType = template.SenderType,
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
                Summary = $"Password reset code emailed to {user.Email} (portal: {portalKey})",
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
                || string.IsNullOrWhiteSpace(request.Code)
                || string.IsNullOrWhiteSpace(request.NewPassword))
            {
                return Result.Failure(ErrorCodes.VALIDATION_ERROR,
                    "Email, code, and new password are required.");
            }

            if (!string.Equals(request.NewPassword, request.ConfirmPassword, StringComparison.Ordinal))
                return Result.Failure(ErrorCodes.VALIDATION_ERROR, "Passwords do not match.");

            var user = await _userManager.FindByEmailAsync(request.Email.Trim());
            if (user is null)
            {
                // Friendly message that doesn't confirm or deny the email.
                return Result.Failure(ErrorCodes.UNAUTHORIZED,
                    "This code is no longer valid. Please request a new one.");
            }

            // Phase 35C — validate the 6-digit OTP against the most
            // recent PasswordReset row in VerificationCodes. Mirrors the
            // change-password OTP flow's safety rails: expiry, attempts,
            // fixed-time compare, consume on success.
            var now = DateTime.UtcNow;
            var record = await _dbContext.VerificationCodes
                .Where(c => c.UserId == user.Id
                    && c.Purpose == VerificationCodePurpose.PasswordReset
                    && c.ConsumedAtUtc == null)
                .OrderByDescending(c => c.CreatedAtUtc)
                .FirstOrDefaultAsync();

            if (record is null)
            {
                return Result.Failure(ErrorCodes.VERIFICATION_CODE_INVALID,
                    "This code is invalid. Please request a new one.");
            }

            if (record.ExpiresAtUtc <= now)
            {
                record.ConsumedAtUtc = now;
                await _dbContext.SaveChangesAsync();
                return Result.Failure(ErrorCodes.VERIFICATION_CODE_EXPIRED,
                    "This code has expired. Please request a new one.");
            }

            if (record.AttemptCount >= record.MaxAttempts)
            {
                record.ConsumedAtUtc = now;
                await _dbContext.SaveChangesAsync();
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
                return Result.Failure(ErrorCodes.VERIFICATION_CODE_INVALID,
                    "This code is invalid. Please check it and try again.");
            }

            // Code valid — rotate the password via Identity. We generate
            // a fresh reset token internally and feed it back to
            // ResetPasswordAsync so the standard password validators
            // (length / complexity / etc.) still run.
            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var resetResult = await _userManager.ResetPasswordAsync(user, token, request.NewPassword);
            if (!resetResult.Succeeded)
            {
                var message = string.Join("; ", resetResult.Errors.Select(e => e.Description));
                var hasPasswordError = resetResult.Errors.Any(e =>
                    e.Code.Contains("Password", StringComparison.OrdinalIgnoreCase));
                return Result.Failure(
                    hasPasswordError ? ErrorCodes.WEAK_PASSWORD : ErrorCodes.VALIDATION_ERROR,
                    string.IsNullOrWhiteSpace(message) ? "Could not reset password." : message);
            }

            record.ConsumedAtUtc = now;
            await _dbContext.SaveChangesAsync();

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

    // Phase 35C — same OTP shape for forgot-password.
    private const int PasswordResetCodeLength = 6;
    private const int PasswordResetCodeTtlMinutes = 10;
    private const int PasswordResetMaxAttempts = 5;

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

            // Template owns the body. The full code only ever reaches
            // the rendered email; it is never logged.
            var template = AuthEmailTemplates.ChangePasswordCode(
                firstName: user.FirstName ?? string.Empty,
                code: code,
                expiryMinutes: ChangePasswordCodeTtlMinutes);

            await _notifications.SendAsync(new SendNotificationRequestDto
            {
                UserId = user.Id,
                Channel = NotificationChannel.Email,
                Type = NotificationType.PasswordReset,
                RecipientEmail = user.Email,
                Subject = template.Subject,
                Body = template.PlainTextBody,
                IsHtml = true,
                HtmlBody = template.HtmlBody,
                SenderType = template.SenderType,
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
            var hasProfile = await _dbContext.CustomerProfiles.AnyAsync(p => p.UserId == user.Id);

            // Mirror the JwtTokenGenerator portal-flag logic so /me and
            // /login return identically-shaped CurrentUserDto.
            var isSuperAdmin = roles.Contains(SystemRoles.SuperAdmin, StringComparer.OrdinalIgnoreCase);
            var isAdmin = isSuperAdmin || roles.Contains(SystemRoles.Admin, StringComparer.OrdinalIgnoreCase);
            var isCustomer = roles.Contains(SystemRoles.Customer, StringComparer.OrdinalIgnoreCase);

            var dto = new CurrentUserDto
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email ?? string.Empty,
                PhoneNumber = user.PhoneNumber,
                AccountStatus = user.AccountStatus.ToString(),
                IsActive = user.IsActive,
                Roles = roles.ToList(),
                IsSuperAdmin = isSuperAdmin,
                IsAdmin = isAdmin,
                IsCustomer = isCustomer,
                HasCustomerProfile = hasProfile
            };

            return Result<CurrentUserDto>.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching current user {UserId}", userId);
            return Result<CurrentUserDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred fetching user.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Mobile registration availability probes (Phase 51).
    // Used by the mobile signup wizard so the user gets early feedback
    // BEFORE filling out the remaining steps. Both endpoints return only
    // an `available` boolean — no user identifiers are echoed back so the
    // probe is not a profile-enumeration oracle. Rate limiting still
    // applies via the controller-level AuthPolicy.
    // ─────────────────────────────────────────────────────────────────────

    public async Task<Result<CheckIdentifierAvailableResponseDto>> IsEmailAvailableAsync(string? email)
    {
        var trimmed = NullIfBlank(email);
        if (trimmed is null)
            return Result<CheckIdentifierAvailableResponseDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Email is required.");

        var existing = await _userManager.FindByEmailAsync(trimmed);
        var canonical = trimmed.ToLowerInvariant();
        return Result<CheckIdentifierAvailableResponseDto>.Success(new CheckIdentifierAvailableResponseDto
        {
            Available = existing is null,
            Normalised = canonical
        });
    }

    public async Task<Result<CheckIdentifierAvailableResponseDto>> IsPhoneAvailableAsync(string? phoneNumber)
    {
        var trimmed = NullIfBlank(phoneNumber);
        if (trimmed is null)
            return Result<CheckIdentifierAvailableResponseDto>.Failure(ErrorCodes.VALIDATION_ERROR, "PhoneNumber is required.");

        var normalised = PhoneNumberNormalizer.Normalize(trimmed);
        if (normalised is null)
            return Result<CheckIdentifierAvailableResponseDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Phone number is not in a recognised format.");

        var clash = await _dbContext.Users.AnyAsync(u => u.PhoneNumberNormalized == normalised);
        return Result<CheckIdentifierAvailableResponseDto>.Success(new CheckIdentifierAvailableResponseDto
        {
            Available = !clash,
            Normalised = normalised
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // TEMPORARY dev / UAT OTP login bridge.
    //
    // Accepts identifier + the static dev pin `11111`. When the host
    // environment is non-production AND the identifier resolves to an
    // active user AND the pin matches, mints a real AuthTokenDto via
    // the same token generator as LoginAsync. On production the method
    // hard-fails with FORBIDDEN regardless of the pin so this surface
    // can never authenticate against Live.
    //
    // REMOVAL CHECKLIST when real OTP delivery ships:
    //   1. Drop this method + the matching IAuthService entry.
    //   2. Drop AuthController.DevOtpLogin route.
    //   3. Drop DevOtpLoginRequestDto.
    //   4. Drop the DEV_OTP_PIN constant.
    //   5. Drop the mobile app's apiAuth.devOtpLogin + the temp OTP
    //      branch in authStore.loginWithOtp.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Static dev pin honoured by <see cref="DevOtpLoginAsync"/>.</summary>
    private const string DevOtpPin = "11111";

    public async Task<Result<AuthTokenDto>> DevOtpLoginAsync(DevOtpLoginRequestDto request)
    {
        try
        {
            if (_hostEnvironment.IsProduction())
                return Result<AuthTokenDto>.Failure(ErrorCodes.FORBIDDEN, "OTP login is not available in production.");

            if (request is null
                || string.IsNullOrWhiteSpace(request.EmailOrPhone)
                || string.IsNullOrWhiteSpace(request.Otp))
            {
                return Result<AuthTokenDto>.Failure(ErrorCodes.VALIDATION_ERROR, "EmailOrPhone and Otp are required.");
            }

            // Compare the pin in constant time to keep brute-force timing
            // signal off the wire. The dev pin is short and shared, so this
            // is mostly hygiene — still cheap to do.
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(request.Otp.Trim()),
                    Encoding.UTF8.GetBytes(DevOtpPin)))
            {
                return Result<AuthTokenDto>.Failure(ErrorCodes.INVALID_CREDENTIALS, "Invalid one-time pin.");
            }

            // Identifier lookup — mirror LoginAsync. Email first via Identity
            // (case-insensitive), then phone via direct EF query against the
            // E.164-normalised column.
            var identifier = request.EmailOrPhone.Trim();
            var user = await _userManager.FindByEmailAsync(identifier);
            if (user is null)
            {
                var phoneNormalised = PhoneNumberNormalizer.Normalize(identifier);
                if (phoneNormalised is not null)
                {
                    user = await _userManager.Users
                        .FirstOrDefaultAsync(u => u.PhoneNumberNormalized == phoneNormalised);
                }
            }
            if (user is null)
                return Result<AuthTokenDto>.Failure(ErrorCodes.INVALID_CREDENTIALS, "Account not found.");

            if (!user.IsActive
                || user.AccountStatus == UserAccountStatus.Suspended
                || user.AccountStatus == UserAccountStatus.Inactive)
            {
                return Result<AuthTokenDto>.Failure(ErrorCodes.FORBIDDEN, "Account is not allowed to sign in.");
            }

            var token = await _jwtTokenGenerator.GenerateTokenAsync(user);

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = user.Id,
                ActorType = AuditActorType.User,
                ActionType = AuditActionType.UserLoggedIn,
                EntityType = AuditEntityType.Auth,
                EntityId = user.Id,
                EntityName = user.Email,
                Summary = $"DEV OTP login: {user.Email} (env={_hostEnvironment.EnvironmentName})",
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });

            return Result<AuthTokenDto>.Success(token, "Login successful.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during dev OTP login for {Identifier}", request?.EmailOrPhone);
            return Result<AuthTokenDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred during OTP login.");
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

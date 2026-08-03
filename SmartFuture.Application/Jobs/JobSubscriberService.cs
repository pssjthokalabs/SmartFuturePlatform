using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Auth.Dtos;
using SmartFuture.Application.Common.Interfaces.Identity;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Storage;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Application.Users;
using SmartFuture.Domain.Identity;
using SmartFuture.Domain.Jobs;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Identity;
using SmartFuture.Shared.Enums.Jobs;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;
using SmartFuture.Shared.Utilities;

namespace SmartFuture.Application.Jobs;

// Self-service surface for job seekers: enrolment, profile, CV /
// cover-letter uploads, alert preferences.
//
// Enrolment rules (the part that must never regress):
//   • A signed-in user is ALWAYS upgraded in place — never duplicated.
//   • An anonymous caller whose email already exists is upgraded only
//     after their password is verified; otherwise they are told to sign
//     in, with a distinct error code so the client can route them.
//   • Role addition goes through IUserRoleUpgradeService, which is
//     additive-only. Enrolling as a job seeker can never strip Customer.
public class JobSubscriberService : IJobSubscriberService
{
    private readonly UserManager<User> _userManager;
    private readonly SignInManager<User> _signInManager;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;
    private readonly IUserRoleUpgradeService _roleUpgrades;
    private readonly IAppDbContext _dbContext;
    private readonly IFileStorageService _storage;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<JobSubscriberService> _logger;

    public JobSubscriberService(UserManager<User> userManager, SignInManager<User> signInManager, IJwtTokenGenerator jwtTokenGenerator, IUserRoleUpgradeService roleUpgrades,
        IAppDbContext dbContext, IFileStorageService storage, IAuditService auditService, ICurrentUserService currentUser, ILogger<JobSubscriberService> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _jwtTokenGenerator = jwtTokenGenerator;
        _roleUpgrades = roleUpgrades;
        _dbContext = dbContext;
        _storage = storage;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
    }

    // ─── Enrolment ────────────────────────────────────────────────────

    public async Task<Result<AuthTokenDto>> RegisterAsync(RegisterJobSubscriberRequestDto request, Guid? currentUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request is null)
                return Result<AuthTokenDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            // Path A — already signed in. This is the "existing Customer
            // wants job access" journey: authenticate, then complete the
            // job subscriber profile. Nothing about their Customer role,
            // profile, orders or billing is touched.
            if (currentUserId.HasValue && currentUserId.Value != Guid.Empty)
            {
                var signedInUser = await _userManager.FindByIdAsync(currentUserId.Value.ToString());
                if (signedInUser is null)
                    return Result<AuthTokenDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");
                if (!signedInUser.IsActive || signedInUser.AccountStatus == UserAccountStatus.Suspended)
                    return Result<AuthTokenDto>.Failure(ErrorCodes.FORBIDDEN, "This account is not allowed to enrol.");

                return await CompleteEnrolmentAsync(signedInUser, request, isNewUser: false, cancellationToken);
            }

            // Path B — anonymous. Email + password required.
            var email = JobTextUtilities.NullIfBlank(request.Email);
            if (email is null)
                return Result<AuthTokenDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Email is required.");
            if (string.IsNullOrWhiteSpace(request.Password))
                return Result<AuthTokenDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Password is required.");
            if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
                return Result<AuthTokenDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Passwords do not match.");

            var existing = await _userManager.FindByEmailAsync(email);
            if (existing is not null)
            {
                // Someone already owns this email. NEVER create a second
                // user and never return a bare "email taken" dead end —
                // the whole point of the shared Users table is that this
                // person may already be a Customer.
                if (!existing.IsActive || existing.AccountStatus == UserAccountStatus.Suspended)
                {
                    return Result<AuthTokenDto>.Failure(ErrorCodes.ACCOUNT_EXISTS_SIGN_IN_REQUIRED,
                        "An account already exists for this email but it is not active. Please contact support.");
                }

                var ownershipCheck = await _signInManager.CheckPasswordSignInAsync(existing, request.Password!, lockoutOnFailure: false);
                if (!ownershipCheck.Succeeded)
                {
                    // Knowing an email is not proof of ownership, so we
                    // stop here rather than attaching a role. The client
                    // routes to sign-in and re-calls this endpoint
                    // authenticated (Path A).
                    return Result<AuthTokenDto>.Failure(ErrorCodes.ACCOUNT_EXISTS_SIGN_IN_REQUIRED,
                        "You already have a Smart Future account with this email. Please sign in to activate Job Opportunities on it.");
                }

                return await CompleteEnrolmentAsync(existing, request, isNewUser: false, cancellationToken);
            }

            // Genuinely new person — create the user with the
            // JobSubscriber role ONLY. No Customer role, no
            // CustomerProfile: a job seeker is not an ISP customer until
            // they choose to be.
            var firstName = JobTextUtilities.NullIfBlank(request.FirstName);
            var lastName = JobTextUtilities.NullIfBlank(request.LastName);
            if (firstName is null || lastName is null)
                return Result<AuthTokenDto>.Failure(ErrorCodes.VALIDATION_ERROR, "First name and last name are required.");

            var phoneRaw = JobTextUtilities.NullIfBlank(request.PhoneNumber);
            var phoneNormalized = PhoneNumberNormalizer.Normalize(phoneRaw);
            if (phoneNormalized is not null)
            {
                // Same canonical-phone uniqueness rule the customer
                // registration path enforces — one number, one user.
                var phoneOwner = await _dbContext.Users.FirstOrDefaultAsync(u => u.PhoneNumberNormalized == phoneNormalized, cancellationToken);
                if (phoneOwner is not null)
                {
                    return Result<AuthTokenDto>.Failure(ErrorCodes.ACCOUNT_EXISTS_SIGN_IN_REQUIRED,
                        "That phone number is already linked to a Smart Future account. Please sign in to activate Job Opportunities on it.");
                }
            }

            var user = new User
            {
                UserName = email,
                Email = email,
                PhoneNumber = phoneRaw,
                PhoneNumberNormalized = phoneNormalized,
                FirstName = firstName,
                LastName = lastName,
                AccountStatus = UserAccountStatus.Active,
                IsActive = true,
                IsTestAccount = TestAccountPolicy.IsTestAccountEmail(email),
                CreatedAtUtc = DateTime.UtcNow,
                UserNumber = await UserNumberAllocator.AllocateNextAsync(_dbContext)
            };

            var createResult = await _userManager.CreateAsync(user, request.Password!);
            if (!createResult.Succeeded)
            {
                var message = string.Join("; ", createResult.Errors.Select(e => e.Description));
                var code = createResult.Errors.Any(e => e.Code.Contains("Password", StringComparison.OrdinalIgnoreCase))
                    ? ErrorCodes.WEAK_PASSWORD
                    : ErrorCodes.VALIDATION_ERROR;
                return Result<AuthTokenDto>.Failure(code, message);
            }

            return await CompleteEnrolmentAsync(user, request, isNewUser: true, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during job subscriber registration");
            return Result<AuthTokenDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred during registration.");
        }
    }

    // Shared tail for all three enrolment paths: grant the role, seed the
    // profile + alert preference, mint a token.
    private async Task<Result<AuthTokenDto>> CompleteEnrolmentAsync(User user, RegisterJobSubscriberRequestDto request, bool isNewUser, CancellationToken cancellationToken)
    {
        var roleResult = await _roleUpgrades.EnsureJobSubscriberRoleAsync(user,
            isNewUser ? "New job subscriber registration" : "Job Opportunities enabled on an existing account", cancellationToken);
        if (!roleResult.IsSuccess)
            return Result<AuthTokenDto>.Failure(roleResult.Code ?? ErrorCodes.EXCEPTION, roleResult.Message);

        var profile = await _dbContext.JobSubscriberProfiles.FirstOrDefaultAsync(p => p.UserId == user.Id, cancellationToken);
        if (profile is null)
        {
            profile = new JobSubscriberProfile { UserId = user.Id };
            _dbContext.JobSubscriberProfiles.Add(profile);
        }

        // Only fill blanks — re-running enrolment must not wipe a profile
        // the subscriber already curated.
        profile.CurrentCity ??= JobTextUtilities.NullIfBlank(request.CurrentCity);
        profile.CurrentProvince ??= JobTextUtilities.NullIfBlank(request.CurrentProvince);
        profile.PreferredCategoriesJson ??= JobTextUtilities.WriteStringList(request.PreferredCategories);
        profile.PreferredLocationsJson ??= JobTextUtilities.WriteStringList(request.PreferredLocations);

        await EnsureAlertPreferenceAsync(user.Id, request.SubscribeToAlerts, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = user.Id,
            ActorType = AuditActorType.User,
            ActionType = AuditActionType.JobSubscriberEnrolled,
            EntityType = AuditEntityType.JobSubscriberProfile,
            EntityId = user.Id,
            EntityName = user.Email,
            Summary = isNewUser
                ? $"New job subscriber registered: {user.Email}"
                : $"Job Opportunities enabled on existing account: {user.Email}",
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        });

        var token = await _jwtTokenGenerator.GenerateTokenAsync(user);
        return Result<AuthTokenDto>.Success(token, isNewUser
            ? "Welcome to Smart Future Job Opportunities. Complete your profile to finish."
            : "Job Opportunities is now active on your Smart Future account.");
    }

    private async Task<JobAlertPreference> EnsureAlertPreferenceAsync(Guid userId, bool subscribe, CancellationToken cancellationToken)
    {
        var pref = await _dbContext.JobAlertPreferences.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (pref is null)
        {
            pref = new JobAlertPreference
            {
                UserId = userId,
                IsSubscribed = subscribe,
                UnsubscribedAtUtc = subscribe ? null : DateTime.UtcNow,
                UnsubscribeToken = GenerateUnsubscribeToken()
            };
            _dbContext.JobAlertPreferences.Add(pref);
        }
        else
        {
            pref.UnsubscribeToken ??= GenerateUnsubscribeToken();
        }

        return pref;
    }

    private static string GenerateUnsubscribeToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    // ─── Profile ──────────────────────────────────────────────────────

    public async Task<Result<JobSubscriberMeDto>> GetMeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (userId == Guid.Empty)
                return Result<JobSubscriberMeDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user is null)
                return Result<JobSubscriberMeDto>.Failure(ErrorCodes.NOT_FOUND, "User not found.");

            var roles = await _userManager.GetRolesAsync(user);
            var profile = await _dbContext.JobSubscriberProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
            var alertPref = await _dbContext.JobAlertPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

            var missing = ResolveMissingFields(profile);
            var dto = new JobSubscriberMeDto
            {
                UserId = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email ?? string.Empty,
                PhoneNumber = user.PhoneNumber,
                Roles = roles.ToList(),
                IsJobSubscriber = roles.Contains(SystemRoles.JobSubscriber, StringComparer.OrdinalIgnoreCase),
                IsCustomer = roles.Contains(SystemRoles.Customer, StringComparer.OrdinalIgnoreCase),
                HasProfile = profile is not null,
                IsProfileComplete = profile?.CompletedAtUtc is not null,
                MissingFields = missing,
                Profile = profile is null ? null : MapProfileToDto(profile),
                AlertPreference = alertPref is null ? null : MapAlertPreferenceToDto(alertPref)
            };

            return Result<JobSubscriberMeDto>.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error loading job subscriber {UserId}", userId);
            return Result<JobSubscriberMeDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while loading your profile.");
        }
    }

    public async Task<Result<JobSubscriberProfileDto>> UpsertProfileAsync(Guid userId, UpsertJobSubscriberProfileRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (userId == Guid.Empty)
                return Result<JobSubscriberProfileDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");
            if (request is null)
                return Result<JobSubscriberProfileDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            if (request.YearsOfExperience is < 0 or > 70)
                return Result<JobSubscriberProfileDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Years of experience must be between 0 and 70.");

            var linkedIn = JobTextUtilities.NullIfBlank(request.LinkedInProfileUrl);
            if (linkedIn is not null && !IsHttpUrl(linkedIn))
                return Result<JobSubscriberProfileDto>.Failure(ErrorCodes.VALIDATION_ERROR, "LinkedIn profile must be a valid http(s) link.");

            var website = JobTextUtilities.NullIfBlank(request.WebsiteUrl);
            if (website is not null && !IsHttpUrl(website))
                return Result<JobSubscriberProfileDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Website must be a valid http(s) link.");

            var profile = await _dbContext.JobSubscriberProfiles.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
            if (profile is null)
            {
                profile = new JobSubscriberProfile { UserId = userId };
                _dbContext.JobSubscriberProfiles.Add(profile);
            }

            if (request.PreferredFirstName is not null) profile.PreferredFirstName = JobTextUtilities.NullIfBlank(request.PreferredFirstName);
            if (request.ContactPhone is not null)
            {
                profile.ContactPhone = JobTextUtilities.NullIfBlank(request.ContactPhone);
                profile.ContactPhoneNormalized = PhoneNumberNormalizer.Normalize(profile.ContactPhone);
            }
            if (request.CurrentCity is not null) profile.CurrentCity = JobTextUtilities.NullIfBlank(request.CurrentCity);
            if (request.CurrentProvince is not null) profile.CurrentProvince = JobTextUtilities.NullIfBlank(request.CurrentProvince);
            if (request.CurrentCountry is not null) profile.CurrentCountry = JobTextUtilities.NullIfBlank(request.CurrentCountry);
            if (request.LinkedInProfileUrl is not null) profile.LinkedInProfileUrl = linkedIn;
            if (request.WebsiteUrl is not null) profile.WebsiteUrl = website;
            if (request.SalaryExpectations is not null) profile.SalaryExpectations = JobTextUtilities.NullIfBlank(request.SalaryExpectations);
            if (request.HighestQualification is not null) profile.HighestQualification = JobTextUtilities.NullIfBlank(request.HighestQualification);
            if (request.YearsOfExperience.HasValue) profile.YearsOfExperience = request.YearsOfExperience;
            if (request.PreferredCategories is not null) profile.PreferredCategoriesJson = JobTextUtilities.WriteStringList(request.PreferredCategories);
            if (request.PreferredLocations is not null) profile.PreferredLocationsJson = JobTextUtilities.WriteStringList(request.PreferredLocations);

            ApplyCompletionStamp(profile);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = userId,
                ActorType = AuditActorType.User,
                ActionType = AuditActionType.JobSubscriberProfileUpdated,
                EntityType = AuditEntityType.JobSubscriberProfile,
                EntityId = userId,
                Summary = "Job subscriber profile updated.",
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });

            return Result<JobSubscriberProfileDto>.Success(MapProfileToDto(profile), "Profile saved.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error saving job subscriber profile for {UserId}", userId);
            return Result<JobSubscriberProfileDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while saving your profile.");
        }
    }

    // ─── Documents ────────────────────────────────────────────────────

    public async Task<Result<JobSubscriberDocumentDto>> UploadDocumentAsync(Guid userId, JobSubscriberDocumentType documentType, Stream content, string fileName,
        string? contentType, long sizeBytes, CancellationToken cancellationToken = default)
    {
        try
        {
            if (userId == Guid.Empty)
                return Result<JobSubscriberDocumentDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");
            if (content is null || sizeBytes <= 0)
                return Result<JobSubscriberDocumentDto>.Failure(ErrorCodes.BAD_REQUEST, "A file is required.");
            if (sizeBytes > JobDocumentRules.MaxDocumentSizeBytes)
            {
                return Result<JobSubscriberDocumentDto>.Failure(ErrorCodes.VALIDATION_ERROR,
                    $"File is too large. Maximum size is {JobDocumentRules.MaxDocumentSizeBytes / 1024 / 1024} MB.");
            }
            if (!JobDocumentRules.IsAllowedExtension(fileName))
            {
                return Result<JobSubscriberDocumentDto>.Failure(ErrorCodes.VALIDATION_ERROR,
                    $"Unsupported file type. Upload a {JobDocumentRules.AllowedExtensionsLabel} file.");
            }

            var documentSlug = documentType == JobSubscriberDocumentType.Cv ? "cv" : "cover-letter";
            var resolvedContentType = JobDocumentRules.ResolveContentType(fileName, contentType);
            var extension = JobDocumentRules.GetExtension(fileName);
            var storedName = $"{Guid.NewGuid():N}{extension}";

            var upload = await _storage.UploadAsync(content, JobDocumentRules.BuildObjectFolder(userId, documentSlug), storedName, resolvedContentType, sizeBytes, cancellationToken);
            if (!upload.IsSuccess)
                return Result<JobSubscriberDocumentDto>.Failure(upload.Code ?? ErrorCodes.EXCEPTION, upload.Message);

            var now = DateTime.UtcNow;
            var originalName = SanitiseDisplayName(fileName);

            // Supersede the previous current document of this type. We
            // keep the row (and the object) so an accidental overwrite is
            // recoverable and the admin can see the replacement history.
            var previous = await _dbContext.JobSubscriberDocuments
                .Where(d => d.UserId == userId && d.DocumentType == documentType && d.IsCurrent)
                .ToListAsync(cancellationToken);
            foreach (var doc in previous) doc.IsCurrent = false;

            _dbContext.JobSubscriberDocuments.Add(new JobSubscriberDocument
            {
                UserId = userId,
                DocumentType = documentType,
                ObjectKey = upload.Data!.StorageKey,
                FileUrl = upload.Data.PublicUrl,
                FileName = originalName,
                ContentType = resolvedContentType,
                SizeBytes = sizeBytes,
                IsCurrent = true,
                UploadedAtUtc = now
            });

            var profile = await _dbContext.JobSubscriberProfiles.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
            if (profile is null)
            {
                profile = new JobSubscriberProfile { UserId = userId };
                _dbContext.JobSubscriberProfiles.Add(profile);
            }

            if (documentType == JobSubscriberDocumentType.Cv)
            {
                profile.CvObjectKey = upload.Data.StorageKey;
                profile.CvFileUrl = upload.Data.PublicUrl;
                profile.CvFileName = originalName;
                profile.CvContentType = resolvedContentType;
                profile.CvSizeBytes = sizeBytes;
                profile.CvUploadedAtUtc = now;
            }
            else
            {
                profile.CoverLetterObjectKey = upload.Data.StorageKey;
                profile.CoverLetterFileUrl = upload.Data.PublicUrl;
                profile.CoverLetterFileName = originalName;
                profile.CoverLetterContentType = resolvedContentType;
                profile.CoverLetterSizeBytes = sizeBytes;
                profile.CoverLetterUploadedAtUtc = now;
            }

            ApplyCompletionStamp(profile);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = userId,
                ActorType = AuditActorType.User,
                ActionType = AuditActionType.JobSubscriberDocumentUploaded,
                EntityType = AuditEntityType.JobSubscriberProfile,
                EntityId = userId,
                Summary = $"{documentSlug} uploaded ({originalName}, {sizeBytes} bytes).",
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });

            return Result<JobSubscriberDocumentDto>.Success(new JobSubscriberDocumentDto
            {
                DocumentType = documentType,
                DocumentTypeLabel = documentType.ToString(),
                FileName = originalName,
                ContentType = resolvedContentType,
                SizeBytes = sizeBytes,
                UploadedAtUtc = now
            }, documentType == JobSubscriberDocumentType.Cv ? "CV uploaded." : "Cover letter uploaded.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error uploading {DocumentType} for {UserId}", documentType, userId);
            return Result<JobSubscriberDocumentDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while uploading your document.");
        }
    }

    public async Task<Result<FileDownloadResultDto>> DownloadOwnDocumentAsync(Guid userId, JobSubscriberDocumentType documentType, CancellationToken cancellationToken = default)
    {
        try
        {
            if (userId == Guid.Empty)
                return Result<FileDownloadResultDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            var profile = await _dbContext.JobSubscriberProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
            var key = documentType == JobSubscriberDocumentType.Cv ? profile?.CvObjectKey : profile?.CoverLetterObjectKey;
            var name = documentType == JobSubscriberDocumentType.Cv ? profile?.CvFileName : profile?.CoverLetterFileName;

            if (string.IsNullOrWhiteSpace(key))
                return Result<FileDownloadResultDto>.Failure(ErrorCodes.NOT_FOUND, "No document has been uploaded yet.");

            var download = await _storage.DownloadAsync(key, cancellationToken);
            if (!download.IsSuccess) return download;

            // Prefer the name the subscriber uploaded over the storage
            // key's guid filename.
            if (!string.IsNullOrWhiteSpace(name)) download.Data!.FileName = name!;
            return download;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error downloading own {DocumentType} for {UserId}", documentType, userId);
            return Result<FileDownloadResultDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching your document.");
        }
    }

    // ─── Alerts ───────────────────────────────────────────────────────

    public async Task<Result<JobAlertPreferenceDto>> GetAlertPreferenceAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (userId == Guid.Empty)
                return Result<JobAlertPreferenceDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            var pref = await EnsureAlertPreferenceAsync(userId, subscribe: true, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Result<JobAlertPreferenceDto>.Success(MapAlertPreferenceToDto(pref));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error loading alert preference for {UserId}", userId);
            return Result<JobAlertPreferenceDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while loading your alert settings.");
        }
    }

    public async Task<Result<JobAlertPreferenceDto>> UpdateAlertPreferenceAsync(Guid userId, UpdateJobAlertPreferenceRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (userId == Guid.Empty)
                return Result<JobAlertPreferenceDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");
            if (request is null)
                return Result<JobAlertPreferenceDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var pref = await EnsureAlertPreferenceAsync(userId, subscribe: true, cancellationToken);

            if (request.IsSubscribed.HasValue)
            {
                pref.IsSubscribed = request.IsSubscribed.Value;
                pref.UnsubscribedAtUtc = request.IsSubscribed.Value ? null : DateTime.UtcNow;
            }
            if (request.Frequency.HasValue) pref.Frequency = request.Frequency.Value;
            if (request.Categories is not null) pref.CategoriesJson = JobTextUtilities.WriteStringList(request.Categories);
            if (request.Locations is not null) pref.LocationsJson = JobTextUtilities.WriteStringList(request.Locations);
            if (request.Keywords is not null) pref.KeywordsJson = JobTextUtilities.WriteStringList(request.Keywords);

            await _dbContext.SaveChangesAsync(cancellationToken);

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = userId,
                ActorType = AuditActorType.User,
                ActionType = AuditActionType.JobAlertPreferenceUpdated,
                EntityType = AuditEntityType.JobSubscriberProfile,
                EntityId = userId,
                Summary = $"Job alert preference updated (subscribed={pref.IsSubscribed}, frequency={pref.Frequency}).",
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });

            return Result<JobAlertPreferenceDto>.Success(MapAlertPreferenceToDto(pref), "Alert settings saved.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating alert preference for {UserId}", userId);
            return Result<JobAlertPreferenceDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while saving your alert settings.");
        }
    }

    public async Task<Result> UnsubscribeByTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        try
        {
            var trimmed = JobTextUtilities.NullIfBlank(token);
            if (trimmed is null)
                return Result.Failure(ErrorCodes.BAD_REQUEST, "An unsubscribe token is required.");

            var pref = await _dbContext.JobAlertPreferences.FirstOrDefaultAsync(p => p.UnsubscribeToken == trimmed, cancellationToken);
            if (pref is null)
            {
                // Don't confirm or deny whether the token ever existed —
                // an already-used link and a bogus link look identical.
                return Result.Success("You have been unsubscribed from job alerts.");
            }

            if (pref.IsSubscribed)
            {
                pref.IsSubscribed = false;
                pref.UnsubscribedAtUtc = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return Result.Success("You have been unsubscribed from job alerts.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing unsubscribe token");
            return Result.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while unsubscribing.");
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    // A profile counts as complete once a CV plus the minimum contact /
    // location context is present. Cover letter is explicitly optional.
    private static IReadOnlyList<string> ResolveMissingFields(JobSubscriberProfile? profile)
    {
        var missing = new List<string>();
        if (profile is null)
        {
            missing.Add("profile");
            missing.Add("cv");
            return missing;
        }

        if (string.IsNullOrWhiteSpace(profile.CvObjectKey)) missing.Add("cv");
        if (string.IsNullOrWhiteSpace(profile.CurrentCity) && string.IsNullOrWhiteSpace(profile.CurrentProvince)) missing.Add("currentLocation");
        return missing;
    }

    private static void ApplyCompletionStamp(JobSubscriberProfile profile)
    {
        var complete = ResolveMissingFields(profile).Count == 0;
        if (complete && profile.CompletedAtUtc is null) profile.CompletedAtUtc = DateTime.UtcNow;
        // A profile that regresses (e.g. location cleared) loses the
        // stamp so the wizard reopens rather than silently claiming the
        // subscriber is ready.
        else if (!complete) profile.CompletedAtUtc = null;
    }

    private static bool IsHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    // Strip any path components a client may have sent (some browsers
    // send the full local path) and cap the length for the column.
    private static string SanitiseDisplayName(string fileName)
    {
        var name = Path.GetFileName((fileName ?? "document").Trim());
        if (string.IsNullOrWhiteSpace(name)) name = "document";
        return name.Length > 250 ? name[..250] : name;
    }

    internal static JobSubscriberProfileDto MapProfileToDto(JobSubscriberProfile p) => new()
    {
        Id = p.Id,
        UserId = p.UserId,
        PreferredFirstName = p.PreferredFirstName,
        ContactPhone = p.ContactPhone,
        CurrentCity = p.CurrentCity,
        CurrentProvince = p.CurrentProvince,
        CurrentCountry = p.CurrentCountry,
        LinkedInProfileUrl = p.LinkedInProfileUrl,
        WebsiteUrl = p.WebsiteUrl,
        SalaryExpectations = p.SalaryExpectations,
        HighestQualification = p.HighestQualification,
        YearsOfExperience = p.YearsOfExperience,
        PreferredCategories = JobTextUtilities.ReadStringList(p.PreferredCategoriesJson),
        PreferredLocations = JobTextUtilities.ReadStringList(p.PreferredLocationsJson),
        HasCv = !string.IsNullOrWhiteSpace(p.CvObjectKey),
        CvFileName = p.CvFileName,
        CvSizeBytes = p.CvSizeBytes,
        CvUploadedAtUtc = p.CvUploadedAtUtc,
        HasCoverLetter = !string.IsNullOrWhiteSpace(p.CoverLetterObjectKey),
        CoverLetterFileName = p.CoverLetterFileName,
        CoverLetterSizeBytes = p.CoverLetterSizeBytes,
        CoverLetterUploadedAtUtc = p.CoverLetterUploadedAtUtc,
        CompletedAtUtc = p.CompletedAtUtc,
        CreatedAtUtc = p.CreatedAtUtc,
        UpdatedAtUtc = p.UpdatedAtUtc
    };

    internal static JobAlertPreferenceDto MapAlertPreferenceToDto(JobAlertPreference p) => new()
    {
        UserId = p.UserId,
        IsSubscribed = p.IsSubscribed,
        Frequency = p.Frequency,
        FrequencyLabel = p.Frequency.ToString(),
        Categories = JobTextUtilities.ReadStringList(p.CategoriesJson),
        Locations = JobTextUtilities.ReadStringList(p.LocationsJson),
        Keywords = JobTextUtilities.ReadStringList(p.KeywordsJson),
        LastSentAtUtc = p.LastSentAtUtc,
        UnsubscribedAtUtc = p.UnsubscribedAtUtc
    };
}

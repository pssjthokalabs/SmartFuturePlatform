using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Payments.Mandates;

public class CustomerPaymentMandateService : ICustomerPaymentMandateService
{
    private readonly IAppDbContext _dbContext;
    private readonly IMandateProtector _protector;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<CustomerPaymentMandateService> _logger;

    public CustomerPaymentMandateService(
        IAppDbContext dbContext,
        IMandateProtector protector,
        IAuditService auditService,
        ICurrentUserService currentUser,
        ILogger<CustomerPaymentMandateService> logger)
    {
        _dbContext = dbContext;
        _protector = protector;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<Result<Guid>> UpsertPaystackMandateAsync(
        UpsertPaystackMandateRequestDto request, CancellationToken cancellationToken = default)
    {
        if (request is null || request.UserId == Guid.Empty)
            return Result<Guid>.Failure(ErrorCodes.BAD_REQUEST, "UserId is required.");
        if (string.IsNullOrWhiteSpace(request.AuthorizationCode))
            return Result<Guid>.Failure(ErrorCodes.VALIDATION_ERROR, "AuthorizationCode is required.");
        if (!request.IsReusable)
        {
            _logger.LogInformation(
                "[PaystackMandate] skipping store for user {UserId} — authorization is not reusable", request.UserId);
            return Result<Guid>.Failure(ErrorCodes.VALIDATION_ERROR, "Authorization is not reusable; mandate not stored.");
        }

        // Idempotency key — (User, Provider, Signature). Paystack's
        // authorization signature is the stable per-card hash.
        var existing = !string.IsNullOrWhiteSpace(request.AuthorizationSignature)
            ? await _dbContext.CustomerPaymentMandates.FirstOrDefaultAsync(
                m => m.UserId == request.UserId
                  && m.Provider == PaymentProviderType.Paystack
                  && m.AuthorizationSignature == request.AuthorizationSignature, cancellationToken)
            : null;

        var now = DateTime.UtcNow;
        var isNew = existing is null;
        var entity = existing ?? new CustomerPaymentMandate
        {
            UserId = request.UserId,
            Provider = PaymentProviderType.Paystack,
            ConsentGivenUtc = now,
            ConsentSource = request.ConsentSource,
            IsActive = true,
            IsDefault = false
        };

        // Always re-encrypt — protector keys may have rotated. Cheap.
        entity.AuthorizationCodeProtected = _protector.Protect(request.AuthorizationCode);
        entity.AuthorizationSignature = request.AuthorizationSignature ?? entity.AuthorizationSignature;
        entity.ProviderCustomerCode = request.ProviderCustomerCode ?? entity.ProviderCustomerCode;
        entity.Channel = request.Channel ?? entity.Channel;
        entity.CardType = request.CardType ?? entity.CardType;
        entity.Bank = request.Bank ?? entity.Bank;
        entity.Last4 = request.Last4 ?? entity.Last4;
        entity.ExpMonth = request.ExpMonth ?? entity.ExpMonth;
        entity.ExpYear = request.ExpYear ?? entity.ExpYear;
        entity.AccountName = request.AccountName ?? entity.AccountName;
        entity.CustomerEmail = request.CustomerEmail ?? entity.CustomerEmail;
        entity.IsReusable = request.IsReusable;
        entity.MetadataJson = request.MetadataJson ?? entity.MetadataJson;
        if (!isNew)
        {
            entity.UpdatedAtUtc = now;
            // Re-activate a previously-deactivated mandate so the
            // customer can pay manually and have us pick up the rails
            // again without a roundtrip through support.
            if (!entity.IsActive)
            {
                entity.IsActive = true;
                entity.ConsentRevokedUtc = null;
                entity.ConsentGivenUtc = now;
            }
        }

        if (isNew) _dbContext.CustomerPaymentMandates.Add(entity);

        // First mandate for this (user, provider) → mark default.
        // Existing rows keep whatever they have — customer can re-pick
        // a default from settings later.
        if (isNew)
        {
            var hasAnyDefault = await _dbContext.CustomerPaymentMandates
                .AnyAsync(m => m.UserId == request.UserId
                            && m.Provider == PaymentProviderType.Paystack
                            && m.IsDefault, cancellationToken);
            if (!hasAnyDefault) entity.IsDefault = true;
        }

        // Go-live: when the mandate is stored from an order-checkout /
        // installation-fee payment path, also flip the customer's
        // AutoBillingEnabled preference to true so the first monthly
        // invoice can be auto-debited without a separate opt-in step.
        // Idempotent — if already enabled, this is a no-op + no audit.
        var autoBillingFlipped = false;
        if (request.AutoEnableAutoBilling)
        {
            var profile = await _dbContext.CustomerProfiles
                .FirstOrDefaultAsync(p => p.UserId == request.UserId, cancellationToken);
            if (profile is not null && !profile.AutoBillingEnabled)
            {
                profile.AutoBillingEnabled = true;
                profile.UpdatedAtUtc = now;
                autoBillingFlipped = true;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = AuditActorType.System,
            ActionType = isNew
                ? AuditActionType.CustomerPaymentMandateStored
                : AuditActionType.CustomerPaymentMandateUpdated,
            EntityType = AuditEntityType.CustomerPaymentMandate,
            EntityId = entity.Id,
            EntityName = MaskLabel(entity),
            Summary = isNew
                ? $"Paystack reusable authorization stored ({MaskLabel(entity)})"
                : $"Paystack authorization refreshed ({MaskLabel(entity)})",
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        }, cancellationToken);

        _logger.LogInformation(
            "[PaystackMandate] {Verb} mandate={MandateId} user={UserId} last4={Last4} default={IsDefault} autoBillingFlipped={AutoBillingFlipped}",
            isNew ? "stored" : "refreshed", entity.Id, entity.UserId, entity.Last4 ?? "(none)", entity.IsDefault, autoBillingFlipped);

        // Audit the auto-flip separately so compliance can trace which
        // checkout enabled the customer's automatic billing without
        // confusing it with a manual settings-page toggle.
        if (autoBillingFlipped)
        {
            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser.UserId,
                ActorType   = AuditActorType.System,
                ActionType  = AuditActionType.CustomerProfileUpdated,
                EntityType  = AuditEntityType.CustomerProfile,
                EntityId    = request.UserId,
                Summary     = $"Customer enabled automatic billing during {request.ConsentSource} (Paystack mandate {MaskLabel(entity)}).",
                IpAddress   = _currentUser.IpAddress,
                UserAgent   = _currentUser.UserAgent,
                IsSuccess   = true
            }, cancellationToken);
        }

        return Result<Guid>.Success(entity.Id);
    }

    public async Task<Result<Guid>> UpsertPayFastMandateAsync(
        UpsertPayFastMandateRequestDto request, CancellationToken cancellationToken = default)
    {
        if (request is null || request.UserId == Guid.Empty)
            return Result<Guid>.Failure(ErrorCodes.BAD_REQUEST, "UserId is required.");
        if (string.IsNullOrWhiteSpace(request.Token))
            return Result<Guid>.Failure(ErrorCodes.VALIDATION_ERROR, "PayFast token is required.");

        // Dedupe key — hash of the token (never the raw token). Stable per
        // saved token so a duplicate ITN updates in place.
        var signature = HashToken(request.Token);

        var existing = await _dbContext.CustomerPaymentMandates.FirstOrDefaultAsync(
            m => m.UserId == request.UserId
              && m.Provider == PaymentProviderType.PayFast
              && m.AuthorizationSignature == signature, cancellationToken);

        var now = DateTime.UtcNow;
        var isNew = existing is null;
        var entity = existing ?? new CustomerPaymentMandate
        {
            UserId = request.UserId,
            Provider = PaymentProviderType.PayFast,
            ConsentGivenUtc = now,
            ConsentSource = request.ConsentSource,
            IsActive = true,
            IsDefault = false
        };

        // Always re-encrypt (protector keys may rotate). PayFast tokenization
        // ITNs do NOT carry card metadata, so Last4/CardType/Bank/Exp stay null.
        entity.AuthorizationCodeProtected = _protector.Protect(request.Token);
        entity.AuthorizationSignature = signature;
        entity.CustomerEmail = request.CustomerEmail ?? entity.CustomerEmail;
        entity.IsReusable = true;
        entity.MetadataJson = request.MetadataJson ?? entity.MetadataJson;
        if (!isNew)
        {
            entity.UpdatedAtUtc = now;
            if (!entity.IsActive)
            {
                entity.IsActive = true;
                entity.ConsentRevokedUtc = null;
                entity.ConsentGivenUtc = now;
            }
        }

        if (isNew) _dbContext.CustomerPaymentMandates.Add(entity);

        // First PayFast mandate for this user → mark default.
        if (isNew)
        {
            var hasAnyDefault = await _dbContext.CustomerPaymentMandates
                .AnyAsync(m => m.UserId == request.UserId
                            && m.Provider == PaymentProviderType.PayFast
                            && m.IsDefault, cancellationToken);
            if (!hasAnyDefault) entity.IsDefault = true;
        }

        // Auto-renewal consent → flip the customer's AutoBillingEnabled
        // opt-in (idempotent). A PayFast token is only captured for the
        // "Auto-renewal" choice, so this mirrors the Paystack mandate path.
        // Uses the EXISTING AutoBillingEnabled column — no migration.
        var autoBillingFlipped = false;
        if (request.AutoEnableAutoBilling)
        {
            var profile = await _dbContext.CustomerProfiles
                .FirstOrDefaultAsync(p => p.UserId == request.UserId, cancellationToken);
            if (profile is not null && !profile.AutoBillingEnabled)
            {
                profile.AutoBillingEnabled = true;
                profile.UpdatedAtUtc = now;
                autoBillingFlipped = true;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = AuditActorType.System,
            ActionType = isNew
                ? AuditActionType.CustomerPaymentMandateStored
                : AuditActionType.CustomerPaymentMandateUpdated,
            EntityType = AuditEntityType.CustomerPaymentMandate,
            EntityId = entity.Id,
            EntityName = MaskLabel(entity),
            Summary = isNew
                ? $"PayFast reusable token stored ({MaskLabel(entity)})"
                : $"PayFast token refreshed ({MaskLabel(entity)})",
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        }, cancellationToken);

        // NOTE: token value is NEVER logged.
        _logger.LogInformation(
            "[PayFastMandate] {Verb} mandate={MandateId} user={UserId} default={IsDefault} reusable={Reusable} autoBillingFlipped={AutoBillingFlipped}",
            isNew ? "stored" : "refreshed", entity.Id, entity.UserId, entity.IsDefault, entity.IsReusable, autoBillingFlipped);

        if (autoBillingFlipped)
        {
            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser.UserId,
                ActorType   = AuditActorType.System,
                ActionType  = AuditActionType.CustomerProfileUpdated,
                EntityType  = AuditEntityType.CustomerProfile,
                EntityId    = request.UserId,
                Summary     = $"Customer enabled automatic billing during {request.ConsentSource} (PayFast mandate {MaskLabel(entity)}).",
                IpAddress   = _currentUser.IpAddress,
                UserAgent   = _currentUser.UserAgent,
                IsSuccess   = true
            }, cancellationToken);
        }

        return Result<Guid>.Success(entity.Id);
    }

    private static string HashToken(string token)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    public async Task<Result<IReadOnlyList<CustomerPaymentMandateDto>>> GetMineAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            return Result<IReadOnlyList<CustomerPaymentMandateDto>>.Failure(ErrorCodes.BAD_REQUEST, "UserId is required.");

        var rows = await _dbContext.CustomerPaymentMandates
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderByDescending(m => m.IsDefault)
            .ThenByDescending(m => m.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return Result<IReadOnlyList<CustomerPaymentMandateDto>>.Success(
            rows.Select(MapToDto).ToList());
    }

    public async Task<Result<CustomerPaymentMandateDto>> SetDefaultAsync(
        Guid userId, Guid mandateId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || mandateId == Guid.Empty)
            return Result<CustomerPaymentMandateDto>.Failure(ErrorCodes.BAD_REQUEST, "UserId and mandateId are required.");

        var target = await _dbContext.CustomerPaymentMandates
            .FirstOrDefaultAsync(m => m.Id == mandateId && m.UserId == userId, cancellationToken);
        if (target is null)
            return Result<CustomerPaymentMandateDto>.Failure(ErrorCodes.NOT_FOUND, "Mandate not found.");
        if (!target.IsActive)
            return Result<CustomerPaymentMandateDto>.Failure(ErrorCodes.CONFLICT, "Cannot default an inactive mandate.");

        var others = await _dbContext.CustomerPaymentMandates
            .Where(m => m.UserId == userId
                     && m.Provider == target.Provider
                     && m.Id != target.Id
                     && m.IsDefault)
            .ToListAsync(cancellationToken);
        foreach (var o in others)
        {
            o.IsDefault = false;
            o.UpdatedAtUtc = DateTime.UtcNow;
        }

        target.IsDefault = true;
        target.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = AuditActorType.User,
            ActionType = AuditActionType.CustomerPaymentMandateUpdated,
            EntityType = AuditEntityType.CustomerPaymentMandate,
            EntityId = target.Id,
            EntityName = MaskLabel(target),
            Summary = $"Default Paystack mandate set ({MaskLabel(target)})",
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        }, cancellationToken);

        return Result<CustomerPaymentMandateDto>.Success(MapToDto(target));
    }

    public async Task<Result> DeactivateAsync(
        Guid userId, Guid mandateId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || mandateId == Guid.Empty)
            return Result.Failure(ErrorCodes.BAD_REQUEST, "UserId and mandateId are required.");

        var target = await _dbContext.CustomerPaymentMandates
            .FirstOrDefaultAsync(m => m.Id == mandateId && m.UserId == userId, cancellationToken);
        if (target is null)
            return Result.Failure(ErrorCodes.NOT_FOUND, "Mandate not found.");

        if (!target.IsActive) return Result.Success("Mandate is already inactive.");

        var now = DateTime.UtcNow;
        target.IsActive = false;
        target.IsDefault = false;
        target.ConsentRevokedUtc = now;
        target.UpdatedAtUtc = now;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = _currentUser.UserId == userId ? AuditActorType.User : AuditActorType.System,
            ActionType = AuditActionType.CustomerPaymentMandateRevoked,
            EntityType = AuditEntityType.CustomerPaymentMandate,
            EntityId = target.Id,
            EntityName = MaskLabel(target),
            Summary = $"Paystack mandate revoked ({MaskLabel(target)})",
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        }, cancellationToken);

        return Result.Success("Mandate deactivated.");
    }

    public async Task<Result<AutoBillingPreferenceDto>> GetAutoBillingPreferenceAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            return Result<AutoBillingPreferenceDto>.Failure(ErrorCodes.BAD_REQUEST, "UserId is required.");

        var dto = await BuildPreferenceDtoAsync(userId, cancellationToken);
        return Result<AutoBillingPreferenceDto>.Success(dto);
    }

    public async Task<Result<AutoBillingPreferenceDto>> UpdateAutoBillingPreferenceAsync(
        Guid userId, UpdateAutoBillingPreferenceRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            return Result<AutoBillingPreferenceDto>.Failure(ErrorCodes.BAD_REQUEST, "UserId is required.");
        if (request is null)
            return Result<AutoBillingPreferenceDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

        var profile = await _dbContext.CustomerProfiles.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (profile is null)
            return Result<AutoBillingPreferenceDto>.Failure(ErrorCodes.NOT_FOUND, "Customer profile not found.");

        // Server-side rule: cannot enable auto-billing unless an active
        // reusable mandate exists. Disabling is always allowed.
        if (request.AutoBillingEnabled && !profile.AutoBillingEnabled)
        {
            var hasMandate = await _dbContext.CustomerPaymentMandates.AnyAsync(
                m => m.UserId == userId
                  && m.IsActive
                  && m.IsReusable, cancellationToken);
            if (!hasMandate)
                return Result<AutoBillingPreferenceDto>.Failure(
                    ErrorCodes.CONFLICT,
                    "Save an active payment method before enabling automatic billing.");
        }

        if (profile.AutoBillingEnabled != request.AutoBillingEnabled)
        {
            profile.AutoBillingEnabled = request.AutoBillingEnabled;
            profile.UpdatedAtUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser.UserId,
                ActorType = _currentUser.UserId == userId ? AuditActorType.User : AuditActorType.System,
                ActionType = AuditActionType.CustomerProfileUpdated,
                EntityType = AuditEntityType.CustomerProfile,
                EntityId = profile.Id,
                Summary = request.AutoBillingEnabled
                    ? "Customer enabled automatic billing."
                    : "Customer disabled automatic billing.",
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            }, cancellationToken);
        }

        var dto = await BuildPreferenceDtoAsync(userId, cancellationToken);
        return Result<AutoBillingPreferenceDto>.Success(dto);
    }

    private async Task<AutoBillingPreferenceDto> BuildPreferenceDtoAsync(Guid userId, CancellationToken cancellationToken)
    {
        var profileEnabled = await _dbContext.CustomerProfiles
            .Where(p => p.UserId == userId)
            .Select(p => (bool?)p.AutoBillingEnabled)
            .FirstOrDefaultAsync(cancellationToken) ?? false;

        var mandates = await _dbContext.CustomerPaymentMandates
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.IsActive && m.IsReusable)
            .Select(m => new { m.IsDefault })
            .ToListAsync(cancellationToken);

        return new AutoBillingPreferenceDto
        {
            AutoBillingEnabled = profileEnabled,
            HasActiveReusableMandate = mandates.Count > 0,
            HasDefaultMandate = mandates.Any(m => m.IsDefault)
        };
    }

    private static CustomerPaymentMandateDto MapToDto(CustomerPaymentMandate m) => new()
    {
        Id = m.Id,
        Provider = m.Provider,
        Channel = m.Channel,
        CardType = m.CardType,
        Bank = m.Bank,
        Last4 = m.Last4,
        ExpMonth = m.ExpMonth,
        ExpYear = m.ExpYear,
        AccountName = m.AccountName,
        CustomerEmail = m.CustomerEmail,
        IsReusable = m.IsReusable,
        IsActive = m.IsActive,
        IsDefault = m.IsDefault,
        ConsentGivenUtc = m.ConsentGivenUtc,
        ConsentRevokedUtc = m.ConsentRevokedUtc,
        ConsentSource = m.ConsentSource,
        LastSuccessfulChargeUtc = m.LastSuccessfulChargeUtc,
        LastFailedChargeUtc = m.LastFailedChargeUtc,
        ConsecutiveFailureCount = m.ConsecutiveFailureCount,
        CreatedAtUtc = m.CreatedAtUtc,
        UpdatedAtUtc = m.UpdatedAtUtc,
        DisplayLabel = MaskLabel(m)
    };

    private static string MaskLabel(CustomerPaymentMandate m)
    {
        var brand = string.IsNullOrWhiteSpace(m.CardType) ? "Card" : m.CardType;
        var last4 = string.IsNullOrWhiteSpace(m.Last4) ? "••••" : m.Last4;
        var bank = string.IsNullOrWhiteSpace(m.Bank) ? null : $" ({m.Bank})";
        return $"{brand} •••• {last4}{bank}";
    }
}

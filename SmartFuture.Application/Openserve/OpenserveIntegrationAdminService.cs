using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Security;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Openserve;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Openserve;

public class OpenserveIntegrationAdminService : IOpenserveIntegrationAdminService
{
    private readonly IAppDbContext _dbContext;
    private readonly IOpenserveApiClient _client;
    private readonly IOpenserveRuntimeConfigProvider _configProvider;
    private readonly IOpenserveSecretProtector _protector;
    private readonly IPackageOpenserveMappingService _packageMappingService;
    private readonly IHostEnvironment _environment;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly DataProtectionKeyRingStatus _keyRing;
    private readonly ILogger<OpenserveIntegrationAdminService> _logger;

    public OpenserveIntegrationAdminService(IAppDbContext dbContext, IOpenserveApiClient client, IOpenserveRuntimeConfigProvider configProvider, IOpenserveSecretProtector protector, IPackageOpenserveMappingService packageMappingService,
        IHostEnvironment environment, IAuditService auditService, ICurrentUserService currentUser, DataProtectionKeyRingStatus keyRing, ILogger<OpenserveIntegrationAdminService> logger)
    {
        _dbContext = dbContext;
        _client = client;
        _configProvider = configProvider;
        _protector = protector;
        _packageMappingService = packageMappingService;
        _environment = environment;
        _auditService = auditService;
        _currentUser = currentUser;
        _keyRing = keyRing;
        _logger = logger;
    }

    // ─── Configuration ──────────────────────────────────────────────

    public async Task<Result<OpenserveConfigurationDto>> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Re-read + re-decrypt from the DB on every admin view, so the
            // console reflects what is persisted right now, never a stale
            // in-memory snapshot.
            await _configProvider.RefreshAsync(cancellationToken);
            var row = await LoadRowAsync(cancellationToken);
            var dto = await BuildConfigurationDtoAsync(row, cancellationToken);
            return Result<OpenserveConfigurationDto>.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error reading Openserve configuration.");
            return Result<OpenserveConfigurationDto>.Failure(ErrorCodes.EXCEPTION, "Could not read Openserve configuration.");
        }
    }

    public async Task<Result<OpenserveConfigurationDto>> UpdateConfigurationAsync(UpdateOpenserveConfigurationRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request is null)
                return Result<OpenserveConfigurationDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var row = await _dbContext.OpenserveIntegrationConfigs
                .FirstOrDefaultAsync(c => c.Id == OpenserveIntegrationConfig.SingletonId, cancellationToken);

            var isNew = row is null;
            row ??= new OpenserveIntegrationConfig { Id = OpenserveIntegrationConfig.SingletonId };

            var previousEnabled = row.Enabled ?? _configProvider.Current.Enabled;
            var apiKeyChanged = false;
            var sharedSecretChanged = false;
            var previousCallbackAuthMode = row.CallbackAuthMode;

            // Values are stored exactly as entered apart from surrounding
            // whitespace — "ws-marut" (isp_tag) and "WS MARUT" (ISPID) are
            // different values and their casing/inner spaces are significant.
            if (request.Enabled.HasValue) row.Enabled = request.Enabled;
            if (request.BaseUrl is not null) row.BaseUrl = NormalizeBaseUrl(request.BaseUrl);
            if (request.WsIspCode is not null) row.WsIspCode = Trim(request.WsIspCode);
            if (request.IspIdentifier is not null) row.IspIdentifier = Trim(request.IspIdentifier);
            if (request.SenderId is not null) row.SenderId = Trim(request.SenderId);
            if (request.ReplyToAddress is not null) row.ReplyToAddress = Trim(request.ReplyToAddress);
            if (request.EventNotificationUrl is not null) row.EventNotificationUrl = Trim(request.EventNotificationUrl);
            if (request.HttpTimeoutSeconds.HasValue) row.HttpTimeoutSeconds = request.HttpTimeoutSeconds;
            if (request.PollingFallbackIntervalMinutes.HasValue) row.PollingFallbackIntervalMinutes = request.PollingFallbackIntervalMinutes;
            if (!string.IsNullOrWhiteSpace(request.CallbackAuthMode)
                && Enum.TryParse<OpenserveCallbackAuthMode>(request.CallbackAuthMode, ignoreCase: true, out var parsedCallbackAuthMode))
            {
                row.CallbackAuthMode = parsedCallbackAuthMode;
            }
            if (request.AllowedIpRanges is not null) row.AllowedIpRangesCsv = string.Join(",", request.AllowedIpRanges.Where(r => !string.IsNullOrWhiteSpace(r)));

            // Secrets: blank/omitted = unchanged (the stored ciphertext is
            // never touched); a typed value = replace; the explicit Clear
            // flag = wipe. A typed value wins over Clear: the console used to
            // keep a pending "Remove" armed after the admin typed a fresh key,
            // and the save then silently wiped the key they had just entered.
            if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                var protectedKey = _protector.ProtectApiKey(request.ApiKey.Trim());
                // Prove the round trip before persisting, so a broken
                // protector can never store a key that won't decrypt.
                if (_protector.UnprotectApiKey(protectedKey) != request.ApiKey.Trim())
                    return Result<OpenserveConfigurationDto>.Failure(ErrorCodes.EXCEPTION, "The API key could not be encrypted reliably on this server; it was not saved.");
                row.ApiKeyProtected = protectedKey;
                apiKeyChanged = true;
            }
            else if (request.ClearApiKey)
            {
                row.ApiKeyProtected = null;
                apiKeyChanged = true;
            }

            if (!string.IsNullOrWhiteSpace(request.SharedSecret))
            {
                row.SharedSecretProtected = _protector.ProtectSharedSecret(request.SharedSecret.Trim());
                sharedSecretChanged = true;
            }
            else if (request.ClearSharedSecret)
            {
                row.SharedSecretProtected = null;
                sharedSecretChanged = true;
            }

            // Validate the RESULTING merged state (not just the delta) —
            // an admin flipping Enabled=true this call while ApiKey was
            // never set must be refused, not silently accepted.
            var effectiveEnabled = row.Enabled ?? _configProvider.Current.Enabled;
            if (effectiveEnabled)
            {
                var issues = ValidateForEnable(row);
                if (issues.Count > 0)
                {
                    return Result<OpenserveConfigurationDto>.Failure(
                        ErrorCodes.VALIDATION_ERROR,
                        "Cannot enable Openserve with incomplete configuration: " + string.Join("; ", issues));
                }
            }

            var urlIssues = ValidateUrls(row);
            if (urlIssues.Count > 0)
            {
                return Result<OpenserveConfigurationDto>.Failure(ErrorCodes.VALIDATION_ERROR, string.Join("; ", urlIssues));
            }

            row.UpdatedByUserId = _currentUser.UserId;
            row.UpdatedAtUtc = DateTime.UtcNow;

            if (isNew)
            {
                row.CreatedAtUtc = DateTime.UtcNow;
                _dbContext.OpenserveIntegrationConfigs.Add(row);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await _configProvider.RefreshAsync(cancellationToken);

            await EmitConfigAuditAsync(previousEnabled, row.Enabled ?? previousEnabled, apiKeyChanged, sharedSecretChanged, previousCallbackAuthMode, row.CallbackAuthMode);

            var dto = await BuildConfigurationDtoAsync(row, cancellationToken);
            return Result<OpenserveConfigurationDto>.Success(dto, "Openserve configuration updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating Openserve configuration.");
            return Result<OpenserveConfigurationDto>.Failure(ErrorCodes.EXCEPTION, "Could not update Openserve configuration.");
        }
    }

    // Every value the Postman collection sends on a Product Ordering call
    // must be present before submission can be switched on.
    private static List<string> ValidateForEnable(OpenserveIntegrationConfig row)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(row.BaseUrl)) issues.Add("Base URL (HOST_URL) is required");
        if (string.IsNullOrWhiteSpace(row.ApiKeyProtected)) issues.Add("API Key (API_KEY) is required");
        if (string.IsNullOrWhiteSpace(row.WsIspCode)) issues.Add("ws-ispcode (isp_tag) is required");
        if (string.IsNullOrWhiteSpace(row.IspIdentifier)) issues.Add("ISP Identifier (ISPID) is required");
        if (string.IsNullOrWhiteSpace(row.SenderId)) issues.Add("Sender ID (SenderID) is required");
        if (string.IsNullOrWhiteSpace(row.ReplyToAddress)) issues.Add("ReplyToAddress is required");
        return issues;
    }

    private static List<string> ValidateUrls(OpenserveIntegrationConfig row)
    {
        var issues = new List<string>();
        if (!IsValidHttpsUrl(row.BaseUrl, out var baseUrlError)) issues.Add($"Base URL: {baseUrlError}");
        if (!IsValidHttpsUrl(row.ReplyToAddress, out var replyError)) issues.Add($"ReplyToAddress: {replyError}");
        if (!IsValidHttpsUrl(row.EventNotificationUrl, out var eventError)) issues.Add($"EventNotificationUrl: {eventError}");
        if (!string.IsNullOrWhiteSpace(row.WsIspCode) && row.WsIspCode.Any(char.IsWhiteSpace))
            issues.Add($"ws-ispcode (isp_tag) is a URL path segment and cannot contain spaces — '{row.WsIspCode}' looks like the ISP Identifier (ISPID) instead");
        return issues;
    }

    /// <summary>Openserve issues HOST_URL as a bare host ("stapitrx.openserve.co.za"); the Postman collection always calls it over https://. A bare host is stored with https:// and no trailing slash. Anything with an explicit scheme is left for ValidateUrls to judge.</summary>
    private static string NormalizeBaseUrl(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return string.Empty;
        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "https://" + trimmed;
        return trimmed.TrimEnd('/');
    }

    /// <summary>Empty is always valid (falls back to appsettings) — only a NON-empty value that isn't a proper HTTPS URL is rejected.</summary>
    private static bool IsValidHttpsUrl(string? value, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            error = "not a valid absolute URL";
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "must be HTTPS";
            return false;
        }
        return true;
    }

    private async Task<OpenserveIntegrationConfig?> LoadRowAsync(CancellationToken cancellationToken)
        => await _dbContext.OpenserveIntegrationConfigs
            .AsNoTracking()
            .Include(c => c.UpdatedByUser)
            .FirstOrDefaultAsync(c => c.Id == OpenserveIntegrationConfig.SingletonId, cancellationToken);

    private Task<OpenserveConfigurationDto> BuildConfigurationDtoAsync(OpenserveIntegrationConfig? row, CancellationToken cancellationToken)
    {
        var current = _configProvider.Current;
        var fallbackApiKeyPresent = !string.IsNullOrWhiteSpace(current.ApiKey);
        var fallbackSharedSecretPresent = !string.IsNullOrWhiteSpace(current.CallbackAuth.SharedSecret);
        var secretState = _configProvider.SecretState;
        var (apiKeyStatus, apiKeyStatusMessage) = DescribeApiKey(current, secretState);

        var dto = new OpenserveConfigurationDto
        {
            Enabled = current.Enabled,
            BaseUrl = current.BaseUrl,
            WsIspCode = current.WsIspCode,
            IspIdentifier = current.IspIdentifier,
            SenderId = current.SenderId,
            ReplyToAddress = current.ReplyToAddress,
            EventNotificationUrl = current.EventNotificationUrl,
            HttpTimeoutSeconds = current.HttpTimeoutSeconds,
            PollingFallbackIntervalMinutes = current.PollingFallbackIntervalMinutes,
            CallbackAuthMode = current.CallbackAuth.Mode.ToString(),
            AllowedIpRanges = current.CallbackAuth.AllowedIpRanges,
            ApiKeyConfigured = fallbackApiKeyPresent,
            ApiKeyMasked = fallbackApiKeyPresent ? Mask(current.ApiKey) : null,
            ApiKeyStatus = apiKeyStatus,
            ApiKeyStatusMessage = apiKeyStatusMessage,
            SharedSecretStatus = secretState?.SharedSecretUnreadable == true
                ? OpenserveSecretStatus.StoredButUnreadable
                : fallbackSharedSecretPresent ? OpenserveSecretStatus.Configured : OpenserveSecretStatus.NotConfigured,
            KeyRingPersistent = _keyRing.IsPersistent,
            KeyRingDescription = _keyRing.Description,
            KeyRingProblem = _keyRing.Problem,
            SharedSecretConfigured = fallbackSharedSecretPresent,
            SharedSecretMasked = fallbackSharedSecretPresent ? Mask(current.CallbackAuth.SharedSecret) : null,
            RetryMaxAttempts = current.Retry.MaxAttempts,
            RetryBaseDelaySeconds = current.Retry.BaseDelaySeconds,
            RetryIsWired = false,
            LastUpdatedAtUtc = row?.UpdatedAtUtc,
            LastUpdatedByUserEmail = row?.UpdatedByUser?.Email
        };

        dto.FieldSources = new Dictionary<string, string>
        {
            [nameof(dto.Enabled)] = row?.Enabled is not null ? "Database" : "AppSettings",
            [nameof(dto.BaseUrl)] = !string.IsNullOrWhiteSpace(row?.BaseUrl) ? "Database" : "AppSettings",
            [nameof(dto.WsIspCode)] = !string.IsNullOrWhiteSpace(row?.WsIspCode) ? "Database" : "AppSettings",
            [nameof(dto.IspIdentifier)] = !string.IsNullOrWhiteSpace(row?.IspIdentifier) ? "Database" : "AppSettings",
            [nameof(dto.SenderId)] = !string.IsNullOrWhiteSpace(row?.SenderId) ? "Database" : "AppSettings",
            [nameof(dto.ReplyToAddress)] = !string.IsNullOrWhiteSpace(row?.ReplyToAddress) ? "Database" : "AppSettings",
            [nameof(dto.EventNotificationUrl)] = !string.IsNullOrWhiteSpace(row?.EventNotificationUrl) ? "Database" : "AppSettings",
            ["ApiKey"] = !string.IsNullOrWhiteSpace(row?.ApiKeyProtected) ? "Database" : "AppSettings",
            [nameof(dto.CallbackAuthMode)] = row?.CallbackAuthMode is not null ? "Database" : "AppSettings",
            ["SharedSecret"] = !string.IsNullOrWhiteSpace(row?.SharedSecretProtected) ? "Database" : "AppSettings"
        };

        return Task.FromResult(dto);
    }

    /// <summary>
    /// The API key's real state. "Stored but unreadable" is reported
    /// explicitly: the ciphertext is still in the database but the
    /// DataProtection key that encrypted it is gone — previously this was
    /// indistinguishable from "never set".
    /// </summary>
    private (string Status, string? Message) DescribeApiKey(OpenserveFulfilmentSettings current, OpenserveSecretState? secretState)
    {
        if (secretState?.ApiKeyUnreadable == true)
        {
            var reason = _keyRing.IsPersistent
                ? "It was encrypted with a server key that is no longer available (saved before the key ring was persisted)."
                : "This server's encryption key ring is not persisted, so every restart makes stored secrets unreadable. " + (_keyRing.Problem ?? string.Empty);
            return (OpenserveSecretStatus.StoredButUnreadable, $"An API key is saved but cannot be decrypted. {reason} Re-enter the API key.".Trim());
        }
        if (!string.IsNullOrWhiteSpace(current.ApiKey)) return (OpenserveSecretStatus.Configured, null);
        return (OpenserveSecretStatus.NotConfigured, null);
    }

    private static string Mask(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return string.Empty;
        var tail = secret.Length > 4 ? secret[^4..] : secret;
        return new string('•', 8) + tail;
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private async Task EmitConfigAuditAsync(bool previousEnabled, bool newEnabled, bool apiKeyChanged, bool sharedSecretChanged, OpenserveCallbackAuthMode? previousMode,
        OpenserveCallbackAuthMode? newMode)
    {
        try
        {
            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser.UserId,
                ActorType = AuditActorType.Admin,
                ActionType = AuditActionType.OpenserveConfigurationChanged,
                EntityType = AuditEntityType.OpenserveIntegrationConfig,
                EntityId = OpenserveIntegrationConfig.SingletonId,
                EntityName = "Openserve Integration Configuration",
                Summary = "Openserve integration configuration updated by admin.",
                // Never secret VALUES — only whether they changed.
                MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    enabledBefore = previousEnabled,
                    enabledAfter = newEnabled,
                    apiKeyChanged,
                    sharedSecretChanged,
                    callbackAuthModeBefore = previousMode?.ToString(),
                    callbackAuthModeAfter = newMode?.ToString()
                }),
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });

            if (previousEnabled != newEnabled)
            {
                await _auditService.LogAsync(new CreateAuditLogRequestDto
                {
                    ActorUserId = _currentUser.UserId,
                    ActorType = AuditActorType.Admin,
                    ActionType = newEnabled ? AuditActionType.OpenserveIntegrationEnabled : AuditActionType.OpenserveIntegrationDisabled,
                    EntityType = AuditEntityType.OpenserveIntegrationConfig,
                    EntityId = OpenserveIntegrationConfig.SingletonId,
                    EntityName = "Openserve Integration Configuration",
                    Summary = newEnabled ? "Openserve integration enabled." : "Openserve integration disabled.",
                    IpAddress = _currentUser.IpAddress,
                    UserAgent = _currentUser.UserAgent,
                    IsSuccess = true
                });
            }

            if (apiKeyChanged)
            {
                await _auditService.LogAsync(new CreateAuditLogRequestDto
                {
                    ActorUserId = _currentUser.UserId,
                    ActorType = AuditActorType.Admin,
                    ActionType = AuditActionType.OpenserveApiKeyReplaced,
                    EntityType = AuditEntityType.OpenserveIntegrationConfig,
                    EntityId = OpenserveIntegrationConfig.SingletonId,
                    EntityName = "Openserve Integration Configuration",
                    Summary = "Openserve API key was replaced or cleared.",
                    IpAddress = _currentUser.IpAddress,
                    UserAgent = _currentUser.UserAgent,
                    IsSuccess = true
                });
            }

            if (previousMode != newMode && newMode.HasValue)
            {
                await _auditService.LogAsync(new CreateAuditLogRequestDto
                {
                    ActorUserId = _currentUser.UserId,
                    ActorType = AuditActorType.Admin,
                    ActionType = AuditActionType.OpenserveCallbackAuthChanged,
                    EntityType = AuditEntityType.OpenserveIntegrationConfig,
                    EntityId = OpenserveIntegrationConfig.SingletonId,
                    EntityName = "Openserve Integration Configuration",
                    Summary = $"Openserve callback auth mode changed: {previousMode} -> {newMode}.",
                    IpAddress = _currentUser.IpAddress,
                    UserAgent = _currentUser.UserAgent,
                    IsSuccess = true
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Openserve configuration-change audit log write failed.");
        }
    }

    // ─── Overview / health ──────────────────────────────────────────

    public async Task<Result<OpenserveIntegrationOverviewDto>> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _configProvider.RefreshAsync(cancellationToken);
            var settings = _configProvider.Current;
            var missing = MissingRequiredConfiguration(settings);
            var configComplete = missing.Count == 0;

            // Only real HTTP attempts (HttpMethod set) count as API calls —
            // a submission blocked before sending (no mapping, no AMID) is
            // logged too, but says nothing about Openserve connectivity.
            var lastSuccess = await _dbContext.OpenserveIntegrationLogs
                .AsNoTracking()
                .Where(l => l.Direction == OpenserveIntegrationDirection.Outbound && l.HttpMethod != null && l.IsSuccess)
                .OrderByDescending(l => l.OccurredAtUtc)
                .Select(l => new { l.OccurredAtUtc, l.OperationType })
                .FirstOrDefaultAsync(cancellationToken);

            var lastFailure = await _dbContext.OpenserveIntegrationLogs
                .AsNoTracking()
                .Where(l => l.Direction == OpenserveIntegrationDirection.Outbound && l.HttpMethod != null && !l.IsSuccess)
                .OrderByDescending(l => l.OccurredAtUtc)
                .Select(l => new { l.OccurredAtUtc, l.OperationType, l.ErrorSummary })
                .FirstOrDefaultAsync(cancellationToken);

            var lastCallback = await _dbContext.OpenserveIntegrationLogs
                .AsNoTracking()
                .Where(l => l.Direction == OpenserveIntegrationDirection.Inbound && l.OperationType == OpenserveOperationType.CallbackInbound)
                .OrderByDescending(l => l.OccurredAtUtc)
                .Select(l => (DateTime?)l.OccurredAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            var lastEvent = await _dbContext.OpenserveIntegrationLogs
                .AsNoTracking()
                .Where(l => l.Direction == OpenserveIntegrationDirection.Inbound && l.OperationType == OpenserveOperationType.EventNotificationInbound)
                .OrderByDescending(l => l.OccurredAtUtc)
                .Select(l => (DateTime?)l.OccurredAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            var orderCounts = await _dbContext.OpenserveOrders
                .AsNoTracking()
                .GroupBy(o => 1)
                .Select(g => new
                {
                    Total = g.Count(),
                    NonTerminal = g.Count(o => !o.IsTerminal),
                    Completed = g.Count(o => o.NormalizedStatus == OpenserveProvisioningStatus.Completed),
                    Cancelled = g.Count(o => o.NormalizedStatus == OpenserveProvisioningStatus.Cancelled),
                    Failed = g.Count(o => o.NormalizedStatus == OpenserveProvisioningStatus.Failed),
                    Pending = g.Count(o => !o.IsTerminal && o.NormalizedStatus != OpenserveProvisioningStatus.Failed)
                })
                .FirstOrDefaultAsync(cancellationToken);

            var unmappedResult = await _packageMappingService.ListUnmappedFibrePackagesAsync(cancellationToken);
            var unmappedCount = unmappedResult.IsSuccess ? unmappedResult.Data?.Count ?? 0 : 0;

            var (envLabel, mismatchWarning, mismatchMessage) = InferEnvironment(settings.BaseUrl);

            var dto = new OpenserveIntegrationOverviewDto
            {
                Enabled = settings.Enabled,
                ConfigurationComplete = configComplete,
                MissingConfiguration = missing,
                BaseUrl = string.IsNullOrWhiteSpace(settings.BaseUrl) ? null : settings.BaseUrl,
                EnvironmentLabel = envLabel,
                EnvironmentMismatchWarning = mismatchWarning,
                EnvironmentMismatchMessage = mismatchMessage,
                WsIspCode = string.IsNullOrWhiteSpace(settings.WsIspCode) ? null : settings.WsIspCode,
                IspIdentifier = string.IsNullOrWhiteSpace(settings.IspIdentifier) ? null : settings.IspIdentifier,
                SenderId = string.IsNullOrWhiteSpace(settings.SenderId) ? null : settings.SenderId,
                ApiKeyConfigured = !string.IsNullOrWhiteSpace(settings.ApiKey),
                ApiKeyMasked = string.IsNullOrWhiteSpace(settings.ApiKey) ? null : Mask(settings.ApiKey),
                ApiKeyStatus = DescribeApiKey(settings, _configProvider.SecretState).Status,
                KeyRingPersistent = _keyRing.IsPersistent,
                ReplyToAddress = string.IsNullOrWhiteSpace(settings.ReplyToAddress) ? null : settings.ReplyToAddress,
                EventNotificationUrl = string.IsNullOrWhiteSpace(settings.EventNotificationUrl) ? null : settings.EventNotificationUrl,
                CallbackAuthMode = settings.CallbackAuth.Mode.ToString(),
                ReconciliationRunning = settings.Enabled,
                PollingIntervalMinutes = settings.PollingFallbackIntervalMinutes,
                LastSuccessfulApiCallAtUtc = lastSuccess?.OccurredAtUtc,
                LastSuccessfulApiCallOperation = lastSuccess?.OperationType.ToString(),
                LastFailedApiCallAtUtc = lastFailure?.OccurredAtUtc,
                LastFailedApiCallOperation = lastFailure?.OperationType.ToString(),
                LastFailedApiCallError = lastFailure?.ErrorSummary,
                LastCallbackReceivedAtUtc = lastCallback,
                LastEventNotificationReceivedAtUtc = lastEvent,
                PendingOrders = orderCounts?.Pending ?? 0,
                FailedSubmissions = orderCounts?.Failed ?? 0,
                NonTerminalOrders = orderCounts?.NonTerminal ?? 0,
                CompletedOrders = orderCounts?.Completed ?? 0,
                CancelledOrders = orderCounts?.Cancelled ?? 0,
                TotalOrders = orderCounts?.Total ?? 0,
                UnmappedActiveFibrePackages = unmappedCount
            };

            (dto.OverallState, dto.OverallStateLabel) = ResolveOverallState(dto, lastSuccess?.OccurredAtUtc, lastFailure?.OccurredAtUtc);

            return Result<OpenserveIntegrationOverviewDto>.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building Openserve integration overview.");
            return Result<OpenserveIntegrationOverviewDto>.Failure(ErrorCodes.EXCEPTION, "Could not load the Openserve integration overview.");
        }
    }

    private static List<string> MissingRequiredConfiguration(OpenserveFulfilmentSettings settings)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.BaseUrl)) missing.Add("Base URL");
        if (string.IsNullOrWhiteSpace(settings.ApiKey)) missing.Add("API Key");
        if (string.IsNullOrWhiteSpace(settings.WsIspCode)) missing.Add("ws-ispcode");
        if (string.IsNullOrWhiteSpace(settings.IspIdentifier)) missing.Add("ISP Identifier");
        if (string.IsNullOrWhiteSpace(settings.SenderId)) missing.Add("Sender ID");
        if (string.IsNullOrWhiteSpace(settings.ReplyToAddress)) missing.Add("ReplyToAddress");
        if (string.IsNullOrWhiteSpace(settings.EventNotificationUrl)) missing.Add("EventNotificationUrl");
        return missing;
    }

    /// <summary>Staging/test host markers. "stapitrx" is Smart Future's provisioned Openserve staging/UAT host; "testapitrx" is the PDF's shared test host.</summary>
    private static readonly string[] NonProductionHostMarkers = { "stapitrx", "testapitrx", "staging", "stg", "test", "uat", "sandbox" };

    /// <summary>
    /// Conservative, explicitly-labelled inference — never a bare
    /// "Production" claim. A BaseUrl whose host carries a staging/test
    /// marker (see <see cref="NonProductionHostMarkers"/>) is labelled
    /// "Staging / UAT"; any other non-empty host is labelled "Production
    /// (inferred)" rather than asserted outright (Openserve's production
    /// host is still TBC), and cross-checked against our OWN
    /// ASPNETCORE_ENVIRONMENT for a mismatch warning.
    /// </summary>
    private (string Label, bool MismatchWarning, string? MismatchMessage) InferEnvironment(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return ("Unknown", false, null);

        var host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : baseUrl.ToLowerInvariant();
        var looksLikeTest = NonProductionHostMarkers.Any(marker => host.Contains(marker, StringComparison.Ordinal));
        var label = looksLikeTest ? "Staging / UAT" : "Production (inferred — verify with Openserve)";

        var ourEnvIsProdLike = _environment.IsProduction()
            || string.Equals(_environment.EnvironmentName, "Live", StringComparison.OrdinalIgnoreCase);
        var ourEnvIsUatLike = string.Equals(_environment.EnvironmentName, "UAT", StringComparison.OrdinalIgnoreCase)
            || _environment.IsStaging() || _environment.IsDevelopment();

        if (ourEnvIsUatLike && !looksLikeTest)
        {
            return (label, true, $"This host is running as '{_environment.EnvironmentName}' but the configured Openserve BaseUrl does not look like a test host. Double-check before enabling.");
        }
        if (ourEnvIsProdLike && looksLikeTest)
        {
            return (label, true, $"This host is running as '{_environment.EnvironmentName}' (production-like) but the configured Openserve BaseUrl looks like a TEST host. Real customer orders would go to Openserve's test environment.");
        }
        return (label, false, null);
    }

    private static (string State, string Label) ResolveOverallState(OpenserveIntegrationOverviewDto dto, DateTime? lastSuccessAt, DateTime? lastFailureAt)
    {
        if (!dto.Enabled) return ("Disabled", "Disabled");
        if (!dto.ConfigurationComplete) return ("NotReady", "Not Ready");
        if (dto.UnmappedActiveFibrePackages > 0 && dto.TotalOrders == 0)
        {
            // Configured but nothing provable yet, and package mappings
            // aren't even in place — still short of a demonstrable state.
            return ("NotReady", "Not Ready");
        }
        var recentFailureDominates = lastFailureAt.HasValue && (!lastSuccessAt.HasValue || lastFailureAt > lastSuccessAt);
        if (dto.FailedSubmissions > 0 || recentFailureDominates) return ("Degraded", "Degraded");
        if (dto.TotalOrders == 0) return ("ReadyForUat", "Ready for UAT");
        return ("Healthy", "Healthy");
    }

    // ─── Readiness check ─────────────────────────────────────────────

    public async Task<Result<OpenserveReadinessCheckDto>> RunReadinessCheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _configProvider.RefreshAsync(cancellationToken);
            var settings = _configProvider.Current;
            var (apiKeyStatus, apiKeyStatusMessage) = DescribeApiKey(settings, _configProvider.SecretState);
            var wsIspCodeValid = !string.IsNullOrWhiteSpace(settings.WsIspCode) && !settings.WsIspCode.Any(char.IsWhiteSpace);
            var replyToLooksLikeOurs = LooksLikeSmartFutureCallback(settings.ReplyToAddress);

            var checks = new List<OpenserveReadinessCheckItemDto>
            {
                Check("Enabled", settings.Enabled, settings.Enabled ? null : "Integration is disabled."),
                Check("Base URL (HOST_URL) configured", IsValidHttpsUrlPublic(settings.BaseUrl) && !string.IsNullOrWhiteSpace(settings.BaseUrl), settings.BaseUrl),
                Check("API Key (API_KEY) configured", apiKeyStatus == OpenserveSecretStatus.Configured,
                    apiKeyStatus == OpenserveSecretStatus.Configured ? Mask(settings.ApiKey) : apiKeyStatusMessage),
                Check("Secret encryption key ring persisted", _keyRing.IsPersistent,
                    _keyRing.IsPersistent ? _keyRing.Description : $"{_keyRing.Description} {_keyRing.Problem}".Trim()),
                Check("ws-ispcode (isp_tag) configured", wsIspCodeValid,
                    wsIspCodeValid ? $"{settings.WsIspCode} — URL path segment, and FromLocation on Product Qualification" : "Missing, or contains spaces (looks like the ISPID)."),
                Check("ISP Identifier (ISPID) configured", !string.IsNullOrWhiteSpace(settings.IspIdentifier),
                    string.IsNullOrWhiteSpace(settings.IspIdentifier) ? null : $"{settings.IspIdentifier} — ISP Identifier payload field, and FromLocation on Product Ordering"),
                Check("Sender ID (SenderID) configured", !string.IsNullOrWhiteSpace(settings.SenderId), settings.SenderId),
                Check("ReplyToAddress is the Openserve-provided value", !string.IsNullOrWhiteSpace(settings.ReplyToAddress) && !replyToLooksLikeOurs,
                    replyToLooksLikeOurs
                        ? "This looks like Smart Future's own /api/openserve/callback — Openserve supplies the ReplyToAddress value (e.g. https://stapitrx.openserve.co.za/ws-marut/productordercallback)."
                        : settings.ReplyToAddress),
                Check("Smart Future event endpoint URL recorded", !string.IsNullOrWhiteSpace(settings.EventNotificationUrl),
                    string.IsNullOrWhiteSpace(settings.EventNotificationUrl)
                        ? null
                        : $"{settings.EventNotificationUrl} — registration with Openserve is out-of-band and NOT confirmed by the supplied Postman collection."),
                CheckCallbackAuth(settings),
                await CheckPackageMappingAsync(cancellationToken),
                await CheckMappingCapacitiesAsync(cancellationToken),
                await CheckConnectivityAsync(cancellationToken),
                Check("Reconciliation worker configured", settings.PollingFallbackIntervalMinutes > 0,
                    $"Polls GET /{settings.WsIspCode}/getproductorder/{{id}} for non-terminal orders every {settings.PollingFallbackIntervalMinutes} minute(s) while Enabled.")
            };

            var passed = checks.Count(c => c.Passed);
            var isReady = passed == checks.Count;

            return Result<OpenserveReadinessCheckDto>.Success(new OpenserveReadinessCheckDto
            {
                IsReady = isReady,
                PassedCount = passed,
                TotalCount = checks.Count,
                Summary = isReady ? $"UAT READY — {passed}/{checks.Count} checks passed" : $"NOT READY — {passed}/{checks.Count} checks passed",
                Checks = checks
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error running Openserve readiness check.");
            return Result<OpenserveReadinessCheckDto>.Failure(ErrorCodes.EXCEPTION, "Could not run the readiness check.");
        }
    }

    private static OpenserveReadinessCheckItemDto Check(string name, bool passed, string? detail)
        => new() { Name = name, Passed = passed, Detail = detail };

    private static OpenserveReadinessCheckItemDto CheckCallbackAuth(OpenserveFulfilmentSettings settings)
    {
        var mode = settings.CallbackAuth.Mode;
        var passed = mode switch
        {
            OpenserveCallbackAuthMode.None => true,
            OpenserveCallbackAuthMode.SharedSecretHeader => !string.IsNullOrWhiteSpace(settings.CallbackAuth.SharedSecret),
            OpenserveCallbackAuthMode.IpAllowlist => settings.CallbackAuth.AllowedIpRanges.Length > 0,
            _ => false
        };
        var detail = mode == OpenserveCallbackAuthMode.None
            ? "None — Openserve's PDF lists callback/event authentication as \"N/a\" and the Postman collection documents no scheme."
            : mode.ToString();
        return Check("Callback authentication configured", passed, detail);
    }

    /// <summary>
    /// Every ACTIVE Fibre package must have an ENABLED mapping — the same
    /// rule the submission gate enforces per order. A disabled mapping is
    /// as unavailable as none. Draft / Inactive / Archived Fibre packages
    /// and non-Fibre packages (Security, Voice, LTE, Wireless, …) are not
    /// required.
    /// </summary>
    private async Task<OpenserveReadinessCheckItemDto> CheckPackageMappingAsync(CancellationToken cancellationToken)
    {
        const string name = "All active Fibre packages have an enabled package mapping";
        var unmapped = await _packageMappingService.ListUnmappedFibrePackagesAsync(cancellationToken);
        if (!unmapped.IsSuccess) return Check(name, false, "Could not evaluate package mappings.");

        var missing = unmapped.Data ?? new List<UnmappedServicePackageDto>();
        if (missing.Count == 0) return Check(name, true, "Every active Fibre package has an enabled Openserve mapping.");

        var names = string.Join(", ", missing.Take(6).Select(p => p.MappingStatus == "Disabled" ? $"{p.Name} (mapping disabled)" : p.Name));
        var more = missing.Count > 6 ? $" and {missing.Count - 6} more" : string.Empty;
        return Check(name, false, $"{missing.Count} active Fibre package(s) have no enabled Openserve mapping — orders on these will be blocked at submission: {names}{more}.");
    }

    /// <summary>Every enabled mapping orders the package's own download speed (a 200 Mbps package must not be ordered as OFC 100).</summary>
    private async Task<OpenserveReadinessCheckItemDto> CheckMappingCapacitiesAsync(CancellationToken cancellationToken)
    {
        const string name = "Enabled package mappings match each package's download speed";
        var rows = await _dbContext.PackageOpenserveMappings.AsNoTracking()
            .Where(m => m.IsEnabled && m.ServicePackage != null && m.ServicePackage.Type == ServicePackageType.Fibre && m.ServicePackage.Status == ServicePackageStatus.Active)
            .Select(m => new { m.Sku, m.Capacity, m.CapacityUom, m.ServicePackage!.Name, m.ServicePackage.DownloadSpeedMbps })
            .ToListAsync(cancellationToken);
        var conflicts = rows.Where(r => OpenserveFibreEligibility.MappingCapacityConflict(r.Capacity, r.CapacityUom, r.DownloadSpeedMbps) is not null)
            .Select(r => $"{r.Name} → {r.Sku} {r.Capacity} {r.CapacityUom} (package {r.DownloadSpeedMbps} Mbps)")
            .ToList();
        return conflicts.Count == 0
            ? Check(name, true, "Every enabled mapping's capacity equals its package's download speed.")
            : Check(name, false, $"{conflicts.Count} mapping(s) would order the wrong speed — orders on these are blocked until corrected: {string.Join("; ", conflicts)}.");
    }

    /// <summary>Passes when the most recent outbound Openserve call got an HTTP 2xx back — i.e. host, TLS, api_key and isp_tag were all accepted. Run "Test Connection" to refresh it.</summary>
    private async Task<OpenserveReadinessCheckItemDto> CheckConnectivityAsync(CancellationToken cancellationToken)
    {
        var last = await _dbContext.OpenserveIntegrationLogs
            .AsNoTracking()
            .Where(l => l.Direction == OpenserveIntegrationDirection.Outbound && l.HttpMethod != null)
            .OrderByDescending(l => l.OccurredAtUtc)
            .Select(l => new { l.OccurredAtUtc, l.ResponseStatusCode, l.OperationType, l.ErrorSummary })
            .FirstOrDefaultAsync(cancellationToken);

        if (last is null) return Check("Openserve connectivity verified", false, "No outbound call recorded yet — run Test Connection.");

        var reached = last.ResponseStatusCode is >= 200 and < 300;
        var detail = reached
            ? $"Last call {last.OperationType} at {last.OccurredAtUtc:yyyy-MM-dd HH:mm} UTC returned HTTP {last.ResponseStatusCode}."
            : $"Last call {last.OperationType} at {last.OccurredAtUtc:yyyy-MM-dd HH:mm} UTC failed: {last.ErrorSummary ?? $"HTTP {last.ResponseStatusCode}"}.";
        return Check("Openserve connectivity verified", reached, detail);
    }

    /// <summary>Heuristic guard for the most likely misconfiguration: pasting our own inbound route into the ReplyToAddress header that Openserve itself provides.</summary>
    private static bool LooksLikeSmartFutureCallback(string? replyToAddress)
        => !string.IsNullOrWhiteSpace(replyToAddress)
           && replyToAddress.TrimEnd('/').EndsWith("/api/openserve/callback", StringComparison.OrdinalIgnoreCase);

    // ─── Configuration check (local) / Test Connection (network) ───

    public async Task<Result<OpenserveConfigurationCheckResultDto>> RunConfigurationCheckAsync(CancellationToken cancellationToken = default)
    {
        await _configProvider.RefreshAsync(cancellationToken);
        var settings = _configProvider.Current;
        var issues = new List<string>();

        var (apiKeyStatus, apiKeyStatusMessage) = DescribeApiKey(settings, _configProvider.SecretState);
        if (apiKeyStatus == OpenserveSecretStatus.StoredButUnreadable) issues.Add(apiKeyStatusMessage!);
        if (!_keyRing.IsPersistent)
            issues.Add($"The server's DataProtection key ring is not persisted — a saved API key will become unreadable after the next restart. {_keyRing.Problem}".Trim());

        if (settings.Enabled)
        {
            if (string.IsNullOrWhiteSpace(settings.BaseUrl)) issues.Add("Base URL (HOST_URL) is not set.");
            if (string.IsNullOrWhiteSpace(settings.ApiKey)) issues.Add("API Key (API_KEY) is not set.");
            if (string.IsNullOrWhiteSpace(settings.WsIspCode)) issues.Add("ws-ispcode (isp_tag) is not set.");
            if (string.IsNullOrWhiteSpace(settings.IspIdentifier)) issues.Add("ISP Identifier (ISPID) is not set.");
            if (string.IsNullOrWhiteSpace(settings.SenderId)) issues.Add("Sender ID (SenderID) is not set.");
            if (string.IsNullOrWhiteSpace(settings.ReplyToAddress)) issues.Add("ReplyToAddress is not set.");
        }
        if (!IsValidHttpsUrlPublic(settings.BaseUrl)) issues.Add("Base URL is not a valid HTTPS URL.");
        if (!IsValidHttpsUrlPublic(settings.ReplyToAddress)) issues.Add("ReplyToAddress is not a valid HTTPS URL.");
        if (!IsValidHttpsUrlPublic(settings.EventNotificationUrl)) issues.Add("EventNotificationUrl is not a valid HTTPS URL.");
        if (!string.IsNullOrWhiteSpace(settings.WsIspCode) && settings.WsIspCode.Any(char.IsWhiteSpace))
            issues.Add($"ws-ispcode '{settings.WsIspCode}' contains spaces — isp_tag is a URL path segment (e.g. ws-marut); the spaced value (e.g. WS MARUT) is the ISP Identifier.");
        if (!string.IsNullOrWhiteSpace(settings.WsIspCode) && string.Equals(settings.WsIspCode.Trim(), settings.IspIdentifier?.Trim(), StringComparison.OrdinalIgnoreCase))
            issues.Add("ws-ispcode (isp_tag) and ISP Identifier (ISPID) are the same value — Openserve issues them separately (e.g. ws-marut vs WS MARUT). Check they haven't been copied across.");
        if (LooksLikeSmartFutureCallback(settings.ReplyToAddress))
            issues.Add("ReplyToAddress points at Smart Future's own /api/openserve/callback. The Postman collection defines ReplyToAddress as a callback URL PROVIDED BY OPENSERVE — use the value Openserve supplied.");
        if (settings.CallbackAuth.Mode == OpenserveCallbackAuthMode.SharedSecretHeader && string.IsNullOrWhiteSpace(settings.CallbackAuth.SharedSecret))
            issues.Add("Callback auth mode is Shared Secret Header but no secret is set.");
        if (settings.CallbackAuth.Mode == OpenserveCallbackAuthMode.IpAllowlist && settings.CallbackAuth.AllowedIpRanges.Length == 0)
            issues.Add("Callback auth mode is IP Allowlist but no IP ranges are set.");

        return Result<OpenserveConfigurationCheckResultDto>.Success(new OpenserveConfigurationCheckResultDto
        {
            Valid = issues.Count == 0,
            Issues = issues
        });
    }

    private static bool IsValidHttpsUrlPublic(string? value) => IsValidHttpsUrl(value, out _);

    /// <summary>The AMID used by the Postman collection's own "productQualification AMID" sample — a known MDU address, so a successful probe also exercises BuildingInfo=Y.</summary>
    public const string TestConnectionProbeAmid = "50782408";

    /// <summary>
    /// Connectivity probe = one read-only Product Qualification by AMID
    /// (GET /{isp_tag}/productqualification?AMID=50782408&amp;BuildingInfo=Y),
    /// the documented call that needs no customer data and no order id.
    /// It exercises HOST_URL, TLS, API_KEY, isp_tag, FromLocation and
    /// SenderID. It NEVER places, cancels, ceases or regrades anything.
    /// (The earlier GET /upp/getactions probe was dropped: it is PDF-only,
    /// not under {isp_tag}, and not in the provisioned Postman collection.)
    /// </summary>
    public async Task<Result<OpenserveTestConnectionResultDto>> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        const string probeDescription = "GET /{isp_tag}/productqualification?AMID=" + TestConnectionProbeAmid + "&BuildingInfo=Y (read-only Product Qualification — the Postman collection's sample AMID)";

        try
        {
            await _configProvider.RefreshAsync(cancellationToken);
            var settings = _configProvider.Current;
            if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.WsIspCode))
            {
                return Result<OpenserveTestConnectionResultDto>.Success(new OpenserveTestConnectionResultDto
                {
                    Success = false,
                    Probe = probeDescription,
                    Message = "Base URL (HOST_URL), API Key (API_KEY) and ws-ispcode (isp_tag) must all be configured before a connection can be tested.",
                    TimestampUtc = DateTime.UtcNow
                });
            }

            var stopwatch = Stopwatch.StartNew();
            var result = await _client.QualifyAsync(new OpenserveQualificationQuery { Amid = TestConnectionProbeAmid, BuildingInfo = true }, cancellationToken);
            stopwatch.Stop();
            var now = DateTime.UtcNow;

            _dbContext.OpenserveIntegrationLogs.Add(BuildOutboundLog(result, OpenserveOperationType.ProductQualification, now, openserveOrderId: null, isSuccess: result.ReachedOpenserve));
            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitDiagnosticAuditAsync(AuditActionType.OpenserveTestQualificationRun, "Admin ran the Openserve Test Connection probe (read-only Product Qualification).", new
            {
                probe = "TestConnection",
                reachedOpenserve = result.ReachedOpenserve,
                httpStatusCode = result.HttpStatusCode
            });

            string message;
            if (result.ReachedOpenserve && result.IsSuccess)
                message = $"Connected — Openserve answered the read-only qualification probe (AMID {result.Outcome?.Amid ?? TestConnectionProbeAmid}, FTTH status {result.Outcome?.FtthStatus ?? "n/a"}).";
            else if (result.ReachedOpenserve)
                message = $"Connected — Openserve accepted the request (HTTP {result.HttpStatusCode}) but the probe's qualification result was: {result.ErrorMessage}";
            else
                message = result.ErrorMessage ?? "Could not reach Openserve.";

            return Result<OpenserveTestConnectionResultDto>.Success(new OpenserveTestConnectionResultDto
            {
                Success = result.ReachedOpenserve,
                ProbeBusinessSuccess = result.IsSuccess,
                HttpStatusCode = result.HttpStatusCode,
                OpenserveResultCode = result.ErrorCode,
                Message = message,
                Probe = probeDescription,
                Request = ToDiagnosticRequest(result),
                RawResponseJson = Truncate(result.ResponseBodyJson, 20_000),
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                TimestampUtc = now
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error testing the Openserve connection.");
            return Result<OpenserveTestConnectionResultDto>.Failure(ErrorCodes.EXCEPTION, "Could not test the Openserve connection.");
        }
    }

    private static OpenserveIntegrationLog BuildOutboundLog<TOutcome>(OpenserveApiCallResult<TOutcome> result, OpenserveOperationType operationType, DateTime occurredAtUtc, Guid? openserveOrderId, bool isSuccess) => new()
    {
        Id = Guid.NewGuid(),
        OpenserveOrderId = openserveOrderId,
        Direction = OpenserveIntegrationDirection.Outbound,
        OperationType = operationType,
        MessageId = result.MessageId,
        HttpMethod = result.HttpMethod,
        Endpoint = result.Endpoint,
        RequestHeadersJson = result.RequestHeadersJson,
        RequestBodyJson = string.IsNullOrEmpty(result.RequestBodyJson) ? null : result.RequestBodyJson,
        ResponseStatusCode = result.HttpStatusCode,
        ResponseBodyJson = Truncate(result.ResponseBodyJson, 20_000),
        OccurredAtUtc = occurredAtUtc,
        IsSuccess = isSuccess,
        ErrorSummary = result.IsSuccess ? null : Truncate($"{result.ErrorCode}: {result.ErrorMessage}", 500)
    };

    /// <summary>Turns the client's already-sanitized header JSON into the admin DTO. Defensive re-redaction: even if a future caller passed raw headers, api_key can never leave this method.</summary>
    private static OpenserveDiagnosticRequestDto ToDiagnosticRequest<TOutcome>(OpenserveApiCallResult<TOutcome> result)
    {
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(result.RequestHeadersJson))
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(result.RequestHeadersJson) ?? new();
                foreach (var (name, value) in parsed)
                    headers[name] = OpenserveHeaderRedaction.SecretHeaderNames.Contains(name) ? OpenserveHeaderRedaction.RedactedValue : value;
            }
            catch (System.Text.Json.JsonException)
            {
                // Unreadable header snapshot — show nothing rather than risk leaking.
            }
        }

        return new OpenserveDiagnosticRequestDto
        {
            HttpMethod = result.HttpMethod,
            Endpoint = result.Endpoint,
            MessageId = result.MessageId,
            HttpStatusCode = result.HttpStatusCode,
            RequestHeaders = headers
        };
    }

    // ─── Product Qualification diagnostic ───────────────────────────

    public async Task<Result<OpenserveQualificationTestResultDto>> RunQualificationTestAsync(RunOpenserveQualificationTestRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request is null || (string.IsNullOrWhiteSpace(request.Amid) && (!request.Latitude.HasValue || !request.Longitude.HasValue)))
            {
                return Result<OpenserveQualificationTestResultDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "Supply either an AMID, or both Latitude and Longitude.");
            }

            var usesAmid = !string.IsNullOrWhiteSpace(request.Amid);

            var stopwatch = Stopwatch.StartNew();
            var result = await _client.QualifyAsync(new OpenserveQualificationQuery
            {
                Amid = usesAmid ? request.Amid!.Trim() : null,
                Latitude = usesAmid ? null : request.Latitude,
                Longitude = usesAmid ? null : request.Longitude,
                BuildingInfo = request.BuildingInfo
            }, cancellationToken);
            stopwatch.Stop();
            var now = DateTime.UtcNow;

            _dbContext.OpenserveIntegrationLogs.Add(BuildOutboundLog(result, OpenserveOperationType.ProductQualification, now, openserveOrderId: null, isSuccess: result.IsSuccess));
            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitDiagnosticAuditAsync(AuditActionType.OpenserveTestQualificationRun, "Admin ran a Product Qualification test.", new
            {
                usedAmid = usesAmid,
                usedCoordinates = !usesAmid,
                success = result.IsSuccess
            });

            var dto = new OpenserveQualificationTestResultDto
            {
                Success = result.IsSuccess,
                ErrorMessage = result.IsSuccess ? null : result.ErrorMessage,
                ErrorCode = result.IsSuccess ? null : result.ErrorCode,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                TimestampUtc = now,
                QueryMode = usesAmid ? "AMID" : "LAT/LON",
                Request = ToDiagnosticRequest(result),
                Amid = result.Outcome?.Amid,
                MatchedAddress = result.Outcome?.MatchedAddress,
                Suburb = result.Outcome?.Suburb,
                Town = result.Outcome?.Town,
                Province = result.Outcome?.Province,
                FtthStatus = result.Outcome?.FtthStatus,
                FibreMaxSpeed = result.Outcome?.FibreMaxSpeed,
                FibreMaxSpeedUnit = result.Outcome?.FibreMaxSpeedUnit,
                BuildingNumId = result.Outcome?.BuildingNumId,
                BuildingMatchCount = result.Outcome?.BuildingMatchCount ?? 0,
                AvailableProducts = result.Outcome?.AvailableProducts?
                    .Select(p => new OpenserveQualificationTestProductDto
                    {
                        ProductName = p.ProductName,
                        ProductCode = p.ProductCode,
                        UpstreamSpeed = p.UpstreamSpeed,
                        DownstreamSpeed = p.DownstreamSpeed
                    }).ToList() ?? new List<OpenserveQualificationTestProductDto>(),
                Buildings = result.Outcome?.Buildings?
                    .Select(b => new OpenserveQualificationTestBuildingDto
                    {
                        AmId = b.AmId,
                        BldNumId = b.BldNumId,
                        BldId = b.BldId,
                        FloorId = b.FloorId,
                        Num = b.Num,
                        BuildingName = b.BuildingName,
                        Floor = b.Floor
                    }).ToList() ?? new List<OpenserveQualificationTestBuildingDto>(),
                RawResponseJson = Truncate(result.ResponseBodyJson, 50_000)
            };

            return Result<OpenserveQualificationTestResultDto>.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error running the Openserve qualification test.");
            return Result<OpenserveQualificationTestResultDto>.Failure(ErrorCodes.EXCEPTION, "Could not run the qualification test.");
        }
    }

    // ─── GET Product Order diagnostic ───────────────────────────────

    public async Task<Result<OpenserveOrderLookupTestResultDto>> RunOrderLookupTestAsync(string openserveOrderId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(openserveOrderId))
                return Result<OpenserveOrderLookupTestResultDto>.Failure(ErrorCodes.VALIDATION_ERROR, "An Openserve order id is required.");

            var stopwatch = Stopwatch.StartNew();
            var result = await _client.GetOrderAsync(openserveOrderId.Trim(), cancellationToken);
            stopwatch.Stop();
            var now = DateTime.UtcNow;

            var localMatch = await _dbContext.OpenserveOrders
                .AsNoTracking()
                .Include(o => o.Order)
                .Where(o => o.OpenserveOrderId == openserveOrderId.Trim())
                .Select(o => new
                {
                    o.Id, o.OrderId, OrderNumber = o.Order!.OrderNumber, o.ExternalReferenceNumber,
                    o.SubscriberReferenceNumber, NormalizedStatus = o.NormalizedStatus.ToString(), o.IsTerminal
                })
                .FirstOrDefaultAsync(cancellationToken);

            // Read-only diagnostic: logs the exchange but never feeds the
            // update pipeline — use Orders → Synchronize for that.
            _dbContext.OpenserveIntegrationLogs.Add(BuildOutboundLog(result, OpenserveOperationType.GetOrder, now, localMatch?.Id, isSuccess: result.IsSuccess));
            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitDiagnosticAuditAsync(AuditActionType.OpenserveTestOrderLookupRun, $"Admin looked up Openserve order {openserveOrderId}.", new
            {
                openserveOrderId,
                success = result.IsSuccess,
                knownLocally = localMatch is not null
            });

            var dto = new OpenserveOrderLookupTestResultDto
            {
                Success = result.IsSuccess,
                ErrorMessage = result.IsSuccess ? null : result.ErrorMessage,
                ErrorCode = result.IsSuccess ? null : result.ErrorCode,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                TimestampUtc = now,
                Request = ToDiagnosticRequest(result),
                OpenserveOrderId = result.Outcome?.Id,
                OpenserveOrderName = result.Outcome?.OrderName,
                ServiceOrderNumber = result.Outcome?.ServiceOrderNumber,
                RawState = result.Outcome?.State,
                OrderDate = result.Outcome?.OrderDate,
                OrderType = result.Outcome?.OrderType,
                CircuitNumber = result.Outcome?.CircuitNumber,
                KnownLocally = localMatch is not null,
                LocalOpenserveOrderId = localMatch?.Id,
                LocalOrderId = localMatch?.OrderId,
                LocalOrderNumber = localMatch?.OrderNumber,
                ExternalReferenceNumber = localMatch?.ExternalReferenceNumber,
                SubscriberReferenceNumber = localMatch?.SubscriberReferenceNumber,
                NormalizedStatus = localMatch?.NormalizedStatus,
                IsTerminal = localMatch?.IsTerminal,
                RawResponseJson = result.ResponseBodyJson
            };

            return Result<OpenserveOrderLookupTestResultDto>.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error running the Openserve order lookup test.");
            return Result<OpenserveOrderLookupTestResultDto>.Failure(ErrorCodes.EXCEPTION, "Could not run the order lookup test.");
        }
    }

    private async Task EmitDiagnosticAuditAsync(AuditActionType actionType, string summary, object metadata)
    {
        try
        {
            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser.UserId,
                ActorType = AuditActorType.Admin,
                ActionType = actionType,
                EntityType = AuditEntityType.OpenserveIntegrationConfig,
                EntityId = OpenserveIntegrationConfig.SingletonId,
                EntityName = "Openserve Integration Diagnostics",
                Summary = summary,
                MetadataJson = System.Text.Json.JsonSerializer.Serialize(metadata),
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Openserve diagnostic-run audit log write failed.");
        }
    }

    // ─── Callback / event health ─────────────────────────────────────

    public async Task<Result<OpenserveCallbackHealthDto>> GetCallbackHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = _configProvider.Current;

            var lastCallback = await _dbContext.OpenserveIntegrationLogs
                .AsNoTracking()
                .Where(l => l.Direction == OpenserveIntegrationDirection.Inbound && l.OperationType == OpenserveOperationType.CallbackInbound)
                .OrderByDescending(l => l.OccurredAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            var lastEvent = await _dbContext.OpenserveIntegrationLogs
                .AsNoTracking()
                .Where(l => l.Direction == OpenserveIntegrationDirection.Inbound && l.OperationType == OpenserveOperationType.EventNotificationInbound)
                .OrderByDescending(l => l.OccurredAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            var successCount = await _dbContext.OpenserveIntegrationLogs
                .Where(l => l.Direction == OpenserveIntegrationDirection.Inbound && l.IsSuccess)
                .CountAsync(cancellationToken);
            var failedCount = await _dbContext.OpenserveIntegrationLogs
                .Where(l => l.Direction == OpenserveIntegrationDirection.Inbound && !l.IsSuccess)
                .CountAsync(cancellationToken);

            var recent = await _dbContext.OpenserveIntegrationLogs
                .AsNoTracking()
                .Where(l => l.Direction == OpenserveIntegrationDirection.Inbound)
                .OrderByDescending(l => l.OccurredAtUtc)
                .Take(25)
                .Select(l => new { l.Id, l.OccurredAtUtc, l.OperationType, l.IsSuccess, l.ErrorSummary, l.OpenserveOrderId })
                .ToListAsync(cancellationToken);

            // Match each recent inbound log to the history row it produced
            // (if any) so the table can show event id / raw state without
            // re-parsing stored JSON in the UI.
            var logIds = recent.Select(r => r.Id).ToList();
            var historyByLog = await _dbContext.OpenserveOrderStatusHistories
                .AsNoTracking()
                .Where(h => h.IntegrationLogId != null && logIds.Contains(h.IntegrationLogId!.Value))
                .ToListAsync(cancellationToken);
            var historyLookup = historyByLog
                .Where(h => h.IntegrationLogId.HasValue)
                .GroupBy(h => h.IntegrationLogId!.Value)
                .ToDictionary(g => g.Key, g => g.First());

            var recentEvents = recent.Select(r =>
            {
                historyLookup.TryGetValue(r.Id, out var history);
                return new OpenserveRecentEventDto
                {
                    LogId = r.Id,
                    OccurredAtUtc = r.OccurredAtUtc,
                    Type = r.OperationType == OpenserveOperationType.CallbackInbound ? "Callback" : "Event",
                    EventId = history?.OpenserveEventId,
                    OpenserveOrderId = history is not null ? history.OpenserveOrderId.ToString() : null,
                    RawState = history?.NewRawState,
                    Result = r.IsSuccess ? "Success" : "Failed"
                };
            }).ToList();

            return Result<OpenserveCallbackHealthDto>.Success(new OpenserveCallbackHealthDto
            {
                CallbackUrl = BuildInboundUrl(settings, "callback"),
                EventUrl = BuildInboundUrl(settings, "events"),
                OpenserveReplyToAddress = string.IsNullOrWhiteSpace(settings.ReplyToAddress) ? null : settings.ReplyToAddress,
                InboundRegistrationStatus = InboundRegistrationStatusText,
                CallbackAuthMode = settings.CallbackAuth.Mode.ToString(),
                LastCallbackReceivedAtUtc = lastCallback?.OccurredAtUtc,
                LastEventReceivedAtUtc = lastEvent?.OccurredAtUtc,
                LastEventType = lastEvent is not null ? "Event" : null,
                LastEventId = lastEvent is not null && historyLookup.TryGetValue(lastEvent.Id, out var lastHist) ? lastHist.OpenserveEventId : null,
                LastProcessingResult = lastEvent?.IsSuccess == true ? "Success" : lastEvent is not null ? "Failed" : null,
                SuccessfulInboundCount = successCount,
                FailedInboundCount = failedCount,
                RecentEvents = recentEvents
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building Openserve callback health.");
            return Result<OpenserveCallbackHealthDto>.Failure(ErrorCodes.EXCEPTION, "Could not load callback/event health.");
        }
    }

    public const string InboundRegistrationStatusText =
        "Not confirmed by Openserve. The provisioned Postman collection contains no inbound callback/event payloads, no registration step and no authentication scheme " +
        "(the PDF lists callback/event authentication as \"N/a\" and says Openserve \"will configure\" the ISP's event endpoint). " +
        "ReplyToAddress is an Openserve-hosted URL, so whether — and how — Openserve forwards results to these Smart Future endpoints must be confirmed with Openserve. " +
        "Until then, reconciliation polling of Query Order Details is the confirmed status path.";

    /// <summary>
    /// Smart Future's OWN inbound routes — never the ReplyToAddress header,
    /// which is an Openserve-provided URL. The host is taken from the
    /// recorded EventNotificationUrl when present (that is the public API
    /// host we tell Openserve about); otherwise a placeholder, since this
    /// service has no reliable way to know the API's own public URL.
    /// </summary>
    private static string BuildInboundUrl(OpenserveFulfilmentSettings settings, string path)
    {
        if (!string.IsNullOrWhiteSpace(settings.EventNotificationUrl) && Uri.TryCreate(settings.EventNotificationUrl, UriKind.Absolute, out var eventUri))
        {
            if (path == "events") return settings.EventNotificationUrl;
            return $"{eventUri.Scheme}://{eventUri.Authority}/api/openserve/{path}";
        }
        return $"https://<api-host>/api/openserve/{path}";
    }

    // ─── Integration Logs (global) ───────────────────────────────────

    public async Task<Result<PagedResult<OpenserveIntegrationLogDto>>> SearchIntegrationLogsAsync(OpenserveIntegrationLogFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new OpenserveIntegrationLogFilterRequestDto();
            var page = Math.Max(1, filter.Page);
            var pageSize = Math.Clamp(filter.PageSize, 1, 200);

            var query = _dbContext.OpenserveIntegrationLogs
                .AsNoTracking()
                .Include(l => l.OpenserveOrder)
                .AsQueryable();

            if (filter.Direction.HasValue) query = query.Where(l => l.Direction == filter.Direction.Value);
            if (filter.OperationType.HasValue) query = query.Where(l => l.OperationType == filter.OperationType.Value);
            if (filter.IsSuccess.HasValue) query = query.Where(l => l.IsSuccess == filter.IsSuccess.Value);
            if (filter.OpenserveOrderId.HasValue) query = query.Where(l => l.OpenserveOrderId == filter.OpenserveOrderId.Value);
            if (filter.DateFromUtc.HasValue) query = query.Where(l => l.OccurredAtUtc >= filter.DateFromUtc.Value);
            if (filter.DateToUtc.HasValue) query = query.Where(l => l.OccurredAtUtc <= filter.DateToUtc.Value);
            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var s = filter.Search.Trim();
                query = query.Where(l =>
                    (l.MessageId != null && EF.Functions.Like(l.MessageId, $"%{s}%")) ||
                    (l.Endpoint != null && EF.Functions.Like(l.Endpoint, $"%{s}%")) ||
                    (l.OpenserveOrder != null && EF.Functions.Like(l.OpenserveOrder.ExternalReferenceNumber, $"%{s}%")) ||
                    (l.OpenserveOrder != null && l.OpenserveOrder.OpenserveOrderId != null && EF.Functions.Like(l.OpenserveOrder.OpenserveOrderId, $"%{s}%")));
            }

            var totalCount = await query.CountAsync(cancellationToken);
            var items = await query
                .OrderByDescending(l => l.OccurredAtUtc)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(l => new OpenserveIntegrationLogDto
                {
                    Id = l.Id,
                    OpenserveOrderId = l.OpenserveOrderId,
                    OpenserveOrderExternalReferenceNumber = l.OpenserveOrder != null ? l.OpenserveOrder.ExternalReferenceNumber : null,
                    Direction = l.Direction.ToString(),
                    OperationType = l.OperationType.ToString(),
                    MessageId = l.MessageId,
                    CorrelationId = l.CorrelationId,
                    HttpMethod = l.HttpMethod,
                    Endpoint = l.Endpoint,
                    RequestHeadersJson = l.RequestHeadersJson,
                    RequestBodyJson = l.RequestBodyJson,
                    ResponseStatusCode = l.ResponseStatusCode,
                    ResponseBodyJson = l.ResponseBodyJson,
                    OccurredAtUtc = l.OccurredAtUtc,
                    IsSuccess = l.IsSuccess,
                    ErrorSummary = l.ErrorSummary
                })
                .ToListAsync(cancellationToken);

            return Result<PagedResult<OpenserveIntegrationLogDto>>.Success(
                new PagedResult<OpenserveIntegrationLogDto>(items, page, pageSize, totalCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching Openserve integration logs.");
            return Result<PagedResult<OpenserveIntegrationLogDto>>.Failure(ErrorCodes.EXCEPTION, "Could not load integration logs.");
        }
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max];
    }
}

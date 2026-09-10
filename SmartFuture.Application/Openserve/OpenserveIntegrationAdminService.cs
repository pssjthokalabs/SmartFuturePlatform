using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Openserve;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Openserve;
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
    private readonly ILogger<OpenserveIntegrationAdminService> _logger;

    public OpenserveIntegrationAdminService(
        IAppDbContext dbContext, IOpenserveApiClient client, IOpenserveRuntimeConfigProvider configProvider,
        IOpenserveSecretProtector protector, IPackageOpenserveMappingService packageMappingService,
        IHostEnvironment environment, IAuditService auditService, ICurrentUserService currentUser,
        ILogger<OpenserveIntegrationAdminService> logger)
    {
        _dbContext = dbContext;
        _client = client;
        _configProvider = configProvider;
        _protector = protector;
        _packageMappingService = packageMappingService;
        _environment = environment;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
    }

    // ─── Configuration ──────────────────────────────────────────────

    public async Task<Result<OpenserveConfigurationDto>> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        try
        {
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

    public async Task<Result<OpenserveConfigurationDto>> UpdateConfigurationAsync(
        UpdateOpenserveConfigurationRequestDto request, CancellationToken cancellationToken = default)
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

            if (request.Enabled.HasValue) row.Enabled = request.Enabled;
            if (request.BaseUrl is not null) row.BaseUrl = Trim(request.BaseUrl);
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

            // Secrets: blank = unchanged; explicit Clear flag = wipe.
            // Clear wins if both are somehow sent together.
            if (request.ClearApiKey)
            {
                row.ApiKeyProtected = null;
                apiKeyChanged = true;
            }
            else if (!string.IsNullOrEmpty(request.ApiKey))
            {
                row.ApiKeyProtected = _protector.ProtectApiKey(request.ApiKey);
                apiKeyChanged = true;
            }

            if (request.ClearSharedSecret)
            {
                row.SharedSecretProtected = null;
                sharedSecretChanged = true;
            }
            else if (!string.IsNullOrEmpty(request.SharedSecret))
            {
                row.SharedSecretProtected = _protector.ProtectSharedSecret(request.SharedSecret);
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

    private static List<string> ValidateForEnable(OpenserveIntegrationConfig row)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(row.BaseUrl)) issues.Add("Base URL is required");
        if (string.IsNullOrWhiteSpace(row.ApiKeyProtected)) issues.Add("API Key is required");
        if (string.IsNullOrWhiteSpace(row.WsIspCode)) issues.Add("ws-ispcode is required");
        if (string.IsNullOrWhiteSpace(row.ReplyToAddress)) issues.Add("ReplyToAddress is required");
        return issues;
    }

    private static List<string> ValidateUrls(OpenserveIntegrationConfig row)
    {
        var issues = new List<string>();
        if (!IsValidHttpsUrl(row.BaseUrl, out var baseUrlError)) issues.Add($"Base URL: {baseUrlError}");
        if (!IsValidHttpsUrl(row.ReplyToAddress, out var replyError)) issues.Add($"ReplyToAddress: {replyError}");
        if (!IsValidHttpsUrl(row.EventNotificationUrl, out var eventError)) issues.Add($"EventNotificationUrl: {eventError}");
        return issues;
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

    private static string Mask(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return string.Empty;
        var tail = secret.Length > 4 ? secret[^4..] : secret;
        return new string('•', 8) + tail;
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private async Task EmitConfigAuditAsync(
        bool previousEnabled, bool newEnabled, bool apiKeyChanged, bool sharedSecretChanged,
        OpenserveCallbackAuthMode? previousMode, OpenserveCallbackAuthMode? newMode)
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
            var settings = _configProvider.Current;
            var missing = MissingRequiredConfiguration(settings);
            var configComplete = missing.Count == 0;

            var lastSuccess = await _dbContext.OpenserveIntegrationLogs
                .AsNoTracking()
                .Where(l => l.Direction == OpenserveIntegrationDirection.Outbound && l.IsSuccess)
                .OrderByDescending(l => l.OccurredAtUtc)
                .Select(l => new { l.OccurredAtUtc, l.OperationType })
                .FirstOrDefaultAsync(cancellationToken);

            var lastFailure = await _dbContext.OpenserveIntegrationLogs
                .AsNoTracking()
                .Where(l => l.Direction == OpenserveIntegrationDirection.Outbound && !l.IsSuccess)
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
        if (string.IsNullOrWhiteSpace(settings.ReplyToAddress)) missing.Add("ReplyToAddress");
        if (string.IsNullOrWhiteSpace(settings.EventNotificationUrl)) missing.Add("EventNotificationUrl");
        return missing;
    }

    /// <summary>
    /// Conservative, explicitly-labelled inference — never a bare
    /// "Production" claim. The only Openserve host actually documented
    /// anywhere we have is the shared test host (testapitrx.openserve.co.za);
    /// a BaseUrl containing "test"/"uat"/"sandbox" is labelled Test, a
    /// non-empty BaseUrl WITHOUT any of those markers is labelled
    /// "Production (inferred)" rather than asserted outright, and cross-
    /// checked against our OWN ASPNETCORE_ENVIRONMENT for a mismatch warning.
    /// </summary>
    private (string Label, bool MismatchWarning, string? MismatchMessage) InferEnvironment(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return ("Unknown", false, null);

        var lower = baseUrl.ToLowerInvariant();
        var looksLikeTest = lower.Contains("test") || lower.Contains("uat") || lower.Contains("sandbox");
        var label = looksLikeTest ? "Test" : "Production (inferred — verify with Openserve)";

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
            var settings = _configProvider.Current;
            var checks = new List<OpenserveReadinessCheckItemDto>
            {
                Check("Enabled", settings.Enabled, settings.Enabled ? null : "Integration is disabled."),
                Check("Base URL configured", !string.IsNullOrWhiteSpace(settings.BaseUrl), settings.BaseUrl),
                Check("API Key configured", !string.IsNullOrWhiteSpace(settings.ApiKey), settings.ApiKey.Length > 0 ? "Set" : null),
                Check("ws-ispcode configured", !string.IsNullOrWhiteSpace(settings.WsIspCode), settings.WsIspCode),
                Check("ISP Identifier configured", !string.IsNullOrWhiteSpace(settings.IspIdentifier), settings.IspIdentifier),
                Check("ReplyToAddress configured", !string.IsNullOrWhiteSpace(settings.ReplyToAddress), settings.ReplyToAddress),
                Check("Event notification URL configured", !string.IsNullOrWhiteSpace(settings.EventNotificationUrl), settings.EventNotificationUrl),
                CheckCallbackAuth(settings),
                await CheckPackageMappingAsync(cancellationToken),
                Check("Qualification client configured",
                    !string.IsNullOrWhiteSpace(settings.BaseUrl) && !string.IsNullOrWhiteSpace(settings.ApiKey) && !string.IsNullOrWhiteSpace(settings.WsIspCode),
                    "Uses the same BaseUrl/ApiKey/ws-ispcode as order submission."),
                Check("Callback endpoints known", true, "/api/openserve/callback and /api/openserve/events — fixed routes, always reachable once the API is deployed."),
                Check("Reconciliation worker configured", settings.PollingFallbackIntervalMinutes > 0, $"Polls every {settings.PollingFallbackIntervalMinutes} minute(s) while Enabled.")
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
            ? "None — acceptable for UAT; Openserve does not document a callback auth scheme."
            : mode.ToString();
        return Check("Callback authentication configured", passed, detail);
    }

    private async Task<OpenserveReadinessCheckItemDto> CheckPackageMappingAsync(CancellationToken cancellationToken)
    {
        var mappings = await _packageMappingService.ListAsync(cancellationToken);
        var hasEnabledMapping = mappings.IsSuccess && (mappings.Data?.Any(m => m.IsEnabled) ?? false);
        return Check("At least one enabled package mapping", hasEnabledMapping,
            hasEnabledMapping ? null : "No enabled PackageOpenserveMapping exists — every submission will be blocked.");
    }

    // ─── Configuration check (local) / Test Connection (network) ───

    public Task<Result<OpenserveConfigurationCheckResultDto>> RunConfigurationCheckAsync(CancellationToken cancellationToken = default)
    {
        var settings = _configProvider.Current;
        var issues = new List<string>();

        if (settings.Enabled)
        {
            if (string.IsNullOrWhiteSpace(settings.BaseUrl)) issues.Add("Base URL is not set.");
            if (string.IsNullOrWhiteSpace(settings.ApiKey)) issues.Add("API Key is not set.");
            if (string.IsNullOrWhiteSpace(settings.WsIspCode)) issues.Add("ws-ispcode is not set.");
            if (string.IsNullOrWhiteSpace(settings.ReplyToAddress)) issues.Add("ReplyToAddress is not set.");
        }
        if (!IsValidHttpsUrlPublic(settings.BaseUrl)) issues.Add("Base URL is not a valid HTTPS URL.");
        if (!IsValidHttpsUrlPublic(settings.ReplyToAddress)) issues.Add("ReplyToAddress is not a valid HTTPS URL.");
        if (!IsValidHttpsUrlPublic(settings.EventNotificationUrl)) issues.Add("EventNotificationUrl is not a valid HTTPS URL.");
        if (settings.CallbackAuth.Mode == OpenserveCallbackAuthMode.SharedSecretHeader && string.IsNullOrWhiteSpace(settings.CallbackAuth.SharedSecret))
            issues.Add("Callback auth mode is Shared Secret Header but no secret is set.");
        if (settings.CallbackAuth.Mode == OpenserveCallbackAuthMode.IpAllowlist && settings.CallbackAuth.AllowedIpRanges.Length == 0)
            issues.Add("Callback auth mode is IP Allowlist but no IP ranges are set.");

        return Task.FromResult(Result<OpenserveConfigurationCheckResultDto>.Success(new OpenserveConfigurationCheckResultDto
        {
            Valid = issues.Count == 0,
            Issues = issues
        }));
    }

    private static bool IsValidHttpsUrlPublic(string? value) => IsValidHttpsUrl(value, out _);

    public async Task<Result<OpenserveTestConnectionResultDto>> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = _configProvider.Current;
            if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.IspIdentifier))
            {
                return Result<OpenserveTestConnectionResultDto>.Success(new OpenserveTestConnectionResultDto
                {
                    Success = false,
                    Message = "Base URL, API Key and ISP Identifier must all be configured before a connection can be tested.",
                    TimestampUtc = DateTime.UtcNow
                });
            }

            var stopwatch = Stopwatch.StartNew();
            var result = await _client.TestConnectionAsync(cancellationToken);
            stopwatch.Stop();
            var now = DateTime.UtcNow;

            _dbContext.OpenserveIntegrationLogs.Add(new OpenserveIntegrationLog
            {
                Id = Guid.NewGuid(),
                Direction = OpenserveIntegrationDirection.Outbound,
                OperationType = OpenserveOperationType.GetActions,
                MessageId = result.MessageId,
                HttpMethod = result.HttpMethod,
                Endpoint = result.Endpoint,
                ResponseStatusCode = result.HttpStatusCode,
                ResponseBodyJson = Truncate(result.ResponseBodyJson, 20_000),
                OccurredAtUtc = now,
                IsSuccess = result.IsSuccess,
                ErrorSummary = result.IsSuccess ? null : Truncate($"{result.ErrorCode}: {result.ErrorMessage}", 500)
            });
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result<OpenserveTestConnectionResultDto>.Success(new OpenserveTestConnectionResultDto
            {
                Success = result.IsSuccess,
                HttpStatusCode = result.HttpStatusCode,
                OpenserveResultCode = result.Outcome?.ResultCode?.ToString() ?? result.ErrorCode,
                Message = result.IsSuccess ? (result.Outcome?.ResultMsg ?? "Connected.") : result.ErrorMessage,
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

    // ─── Product Qualification diagnostic ───────────────────────────

    public async Task<Result<OpenserveQualificationTestResultDto>> RunQualificationTestAsync(
        RunOpenserveQualificationTestRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request is null || (string.IsNullOrWhiteSpace(request.Amid) && (!request.Latitude.HasValue || !request.Longitude.HasValue)))
            {
                return Result<OpenserveQualificationTestResultDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "Supply either an AMID, or both Latitude and Longitude.");
            }

            var stopwatch = Stopwatch.StartNew();
            var result = await _client.QualifyAsync(new OpenserveQualificationQuery
            {
                Amid = request.Amid,
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                BuildingInfo = request.BuildingInfo
            }, cancellationToken);
            stopwatch.Stop();
            var now = DateTime.UtcNow;

            _dbContext.OpenserveIntegrationLogs.Add(new OpenserveIntegrationLog
            {
                Id = Guid.NewGuid(),
                Direction = OpenserveIntegrationDirection.Outbound,
                OperationType = OpenserveOperationType.ProductQualification,
                MessageId = result.MessageId,
                HttpMethod = result.HttpMethod,
                Endpoint = result.Endpoint,
                ResponseStatusCode = result.HttpStatusCode,
                ResponseBodyJson = Truncate(result.ResponseBodyJson, 20_000),
                OccurredAtUtc = now,
                IsSuccess = result.IsSuccess,
                ErrorSummary = result.IsSuccess ? null : Truncate($"{result.ErrorCode}: {result.ErrorMessage}", 500)
            });
            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitDiagnosticAuditAsync(AuditActionType.OpenserveTestQualificationRun, "Admin ran a Product Qualification test.", new
            {
                usedAmid = !string.IsNullOrWhiteSpace(request.Amid),
                usedCoordinates = request.Latitude.HasValue && request.Longitude.HasValue,
                success = result.IsSuccess
            });

            var dto = new OpenserveQualificationTestResultDto
            {
                Success = result.IsSuccess,
                ErrorMessage = result.IsSuccess ? null : result.ErrorMessage,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                TimestampUtc = now,
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
                RawResponseJson = result.ResponseBodyJson
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

    public async Task<Result<OpenserveOrderLookupTestResultDto>> RunOrderLookupTestAsync(
        string openserveOrderId, CancellationToken cancellationToken = default)
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

            _dbContext.OpenserveIntegrationLogs.Add(new OpenserveIntegrationLog
            {
                Id = Guid.NewGuid(),
                OpenserveOrderId = localMatch?.Id,
                Direction = OpenserveIntegrationDirection.Outbound,
                OperationType = OpenserveOperationType.GetOrder,
                MessageId = result.MessageId,
                HttpMethod = result.HttpMethod,
                Endpoint = result.Endpoint,
                ResponseStatusCode = result.HttpStatusCode,
                ResponseBodyJson = Truncate(result.ResponseBodyJson, 20_000),
                OccurredAtUtc = now,
                IsSuccess = result.IsSuccess,
                ErrorSummary = result.IsSuccess ? null : Truncate($"{result.ErrorCode}: {result.ErrorMessage}", 500)
            });
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
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                TimestampUtc = now,
                OpenserveOrderId = result.Outcome?.Id,
                OpenserveOrderName = result.Outcome?.OrderName,
                ServiceOrderNumber = result.Outcome?.ServiceOrderNumber,
                RawState = result.Outcome?.State,
                OrderDate = result.Outcome?.OrderDate,
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
                CallbackUrl = BuildRouteUrl(settings, "callback"),
                EventUrl = BuildRouteUrl(settings, "events"),
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

    /// <summary>Uses ReplyToAddress/EventNotificationUrl's own host when configured (what Openserve is ACTUALLY registered with); otherwise a placeholder host, since this admin service has no reliable way to know the API's own public URL.</summary>
    private static string BuildRouteUrl(OpenserveFulfilmentSettings settings, string path)
    {
        var configured = path == "callback" ? settings.ReplyToAddress : settings.EventNotificationUrl;
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        return $"https://<api-host>/api/openserve/{path}";
    }

    // ─── Integration Logs (global) ───────────────────────────────────

    public async Task<Result<PagedResult<OpenserveIntegrationLogDto>>> SearchIntegrationLogsAsync(
        OpenserveIntegrationLogFilterRequestDto filter, CancellationToken cancellationToken = default)
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

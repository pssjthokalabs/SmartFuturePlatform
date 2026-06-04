using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using SmartFuture.API.Configuration;
using SmartFuture.API.HostedServices;
using SmartFuture.API.Middleware;
using SmartFuture.API.Services;
using SmartFuture.Application.Admin;
using SmartFuture.Application.AppVersion;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auth;
using SmartFuture.Application.Billing;
// PaymentSettings lives in SmartFuture.Application.Billing — same namespace as above.
using SmartFuture.Application.Common.Interfaces.Identity;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Coverage;
using SmartFuture.Application.Coverage.Providers;
using SmartFuture.Application.CoverageRequests;
using SmartFuture.Application.CustomerProfiles;
using SmartFuture.Application.Customers.Admin;
using SmartFuture.Application.Users.Admin;
using SmartFuture.Application.Dashboard;
using SmartFuture.Application.Installations;
using SmartFuture.Application.NetworkAccounts;
using SmartFuture.Application.Communication.Sms;
using SmartFuture.Application.Communication.Verification;
using SmartFuture.Application.Communication.WhatsApp;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.OrderIntents;
using SmartFuture.Application.Orders;
// ServiceActivationSettings lives in SmartFuture.Application.Orders.
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Mandates;
using SmartFuture.Application.Payments.Ozow;
using SmartFuture.Application.Payments.PayFast;
using SmartFuture.Application.Payments.Paystack;
using SmartFuture.Application.Privacy;
using SmartFuture.Application.Reports;
using SmartFuture.Application.ServiceChanges;
using SmartFuture.Application.ServicePackages;
using SmartFuture.Application.SupportTickets;
using SmartFuture.Application.Webhooks;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Identity;
using SmartFuture.Infrastructure.Configuration;
using SmartFuture.Infrastructure.Data;
using SmartFuture.Infrastructure.Data.Seeding;
using SmartFuture.Infrastructure.Identity;
using SmartFuture.Infrastructure.NetworkAccounts;
using SmartFuture.Infrastructure.Communication;
using SmartFuture.Infrastructure.Notifications;
using SmartFuture.Infrastructure.Payments;
using SmartFuture.Infrastructure.Payments.Mandates;
using SmartFuture.Infrastructure.Payments.Ozow;
using SmartFuture.Infrastructure.Payments.PayFast;
using SmartFuture.Infrastructure.Payments.Paystack;
using SmartFuture.Infrastructure.Webhooks;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Extensions;

public static class ServiceExtensions
{
    private const string FrontendCorsPolicy = "FrontendCors";

    public static IServiceCollection AddCoreServices(this IServiceCollection services)
    {
        services.AddControllers();
        services.AddEndpointsApiExplorer();

        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "SmartFuture API",
                Version = "v1"
            });

            var jwtScheme = new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Description = "JWT Bearer token. Example: \"Bearer {token}\"",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Reference = new OpenApiReference
                {
                    Id = JwtBearerDefaults.AuthenticationScheme,
                    Type = ReferenceType.SecurityScheme
                }
            };

            c.AddSecurityDefinition(jwtScheme.Reference.Id, jwtScheme);
            c.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                { jwtScheme, Array.Empty<string>() }
            });
        });

        return services;
    }

    public static IServiceCollection AddDatabaseServices(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var (connectionName, connectionString) = ConnectionStringResolver.Resolve(configuration, environment);

        // Log the NAME only — never the connection string value.
        // Console.WriteLine is captured by ASP.NET Core's startup log during host construction.
        Console.WriteLine($"[SmartFuture.Startup] Selected database connection '{connectionName}' for environment '{environment.EnvironmentName}'.");

        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
            }));

        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        return services;
    }

    public static IServiceCollection AddIdentityServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddIdentity<User, IdentityRole<Guid>>(options =>
        {
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequiredLength = 8;

            options.User.RequireUniqueEmail = true;

            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.AllowedForNewUsers = true;

            options.SignIn.RequireConfirmedEmail = false;
            options.SignIn.RequireConfirmedPhoneNumber = false;
        })
        .AddEntityFrameworkStores<AppDbContext>()
        .AddDefaultTokenProviders();

        services.AddOptions<FrontendSettings>()
            .Bind(configuration.GetSection(FrontendSettings.SectionName));

        // Mobile-app version / update-check policy. Read by the public
        // GET /api/app-version/mobile endpoint. Values live in
        // appsettings.json (NOT env vars) so update flags are a quick
        // config-only edit on the host. Defaults are launch-safe.
        services.AddOptions<MobileAppVersionSettings>()
            .Bind(configuration.GetSection(MobileAppVersionSettings.SectionName));

        services.AddOptions<PaymentSettings>()
            .Bind(configuration.GetSection(PaymentSettings.SectionName));

        // Go-live activation policy. Default is production-safe
        // (RequireManualOpenserveActivation=true → admin completes the
        // Openserve activation manually). UAT flips this to false via
        // env var ServiceActivation__RequireManualOpenserveActivation=false
        // so a successful first-monthly-invoice payment activates the
        // service in one step.
        services.AddOptions<ServiceActivationSettings>()
            .Bind(configuration.GetSection(ServiceActivationSettings.SectionName));

        // Post-registration OTP verification config. The UAT super-OTP
        // bypass is gated by IHostEnvironment.IsProduction() inside
        // AuthService — config flags can't enable it on Production.
        services.AddOptions<OtpSettings>()
            .Bind(configuration.GetSection(OtpSettings.SectionName));

        // Phase 52 — Ozow Payments API. Secrets MUST come from env
        // vars (Ozow__SiteCode, Ozow__ApiKey, Ozow__PrivateKey,
        // Ozow__NotifyUrl, …). The repo's appsettings only carries
        // empty placeholders. When un-set, OzowSettings.IsConfigured
        // is false and OzowPaymentInitiator fails-fast with a clear
        // FailureReason instead of attempting a hashless request.
        //
        // Phase 53.2 — startup validation. If ANY of the Ozow secrets
        // are set then ALL of them (including IsTest) must be set —
        // we refuse to start a half-configured Ozow integration that
        // could silently pick a wrong endpoint or send a wrong IsTest
        // value. ValidateOnStart() runs at app build time, not on
        // first request, so the failure shows up immediately in CI/CD.
        services.AddOptions<OzowSettings>()
            .Bind(configuration.GetSection("Ozow"))
            .Validate(o =>
            {
                // Either fully un-set (Ozow not in use → other
                // initiators handle payments) OR fully set with IsTest
                // present. Partial config is rejected.
                var anySecret = !string.IsNullOrWhiteSpace(o.SiteCode)
                    || !string.IsNullOrWhiteSpace(o.ApiKey)
                    || !string.IsNullOrWhiteSpace(o.PrivateKey)
                    || !string.IsNullOrWhiteSpace(o.NotifyUrl);
                if (!anySecret) return true;  // not in use, nothing to validate
                return !string.IsNullOrWhiteSpace(o.SiteCode)
                    && !string.IsNullOrWhiteSpace(o.ApiKey)
                    && !string.IsNullOrWhiteSpace(o.PrivateKey)
                    && !string.IsNullOrWhiteSpace(o.NotifyUrl)
                    && o.IsTest.HasValue;
            },
            "Ozow is partially configured. Set ALL of Ozow:SiteCode, Ozow:ApiKey, Ozow:PrivateKey, Ozow:NotifyUrl AND Ozow:IsTest (true|false). " +
            "We no longer silently default IsTest — live credentials must run with Ozow__IsTest=false against https://api.ozow.com/postpaymentrequest.")
            .ValidateOnStart();

        services.AddOptions<PayFastSettings>()
            .Bind(configuration.GetSection("PayFast"))
            .Validate(o =>
            {
                var anySet = !string.IsNullOrWhiteSpace(o.MerchantId)
                    || !string.IsNullOrWhiteSpace(o.MerchantKey)
                    || !string.IsNullOrWhiteSpace(o.Passphrase)
                    || !string.IsNullOrWhiteSpace(o.NotifyUrl);
                if (!anySet) return true;
                return !string.IsNullOrWhiteSpace(o.MerchantId)
                    && !string.IsNullOrWhiteSpace(o.MerchantKey)
                    && !string.IsNullOrWhiteSpace(o.Passphrase)
                    && !string.IsNullOrWhiteSpace(o.NotifyUrl);
            },
            "PayFast is partially configured. Set ALL of PayFast:MerchantId, PayFast:MerchantKey, PayFast:Passphrase and PayFast:NotifyUrl.")
            .ValidateOnStart();

        // Paystack — primary payment gateway as of 2026-05-29. Bound
        // here so the secret key + callback URL are validated at startup
        // (only when Paystack:Enabled is true). Secrets MUST come from
        // env vars (Paystack__SecretKey, etc.); the repo's appsettings
        // carries empty placeholders only.
        services.AddOptions<PaystackSettings>()
            .Bind(configuration.GetSection("Paystack"))
            .Validate(o =>
            {
                if (!o.Enabled) return true;
                return !string.IsNullOrWhiteSpace(o.SecretKey)
                    && !string.IsNullOrWhiteSpace(o.CallbackUrl);
            },
            "Paystack is enabled but missing required fields. When Paystack:Enabled=true, set Paystack:SecretKey AND Paystack:CallbackUrl (env: Paystack__SecretKey, Paystack__CallbackUrl).")
            .ValidateOnStart();

        services.AddOptions<JwtSettings>()
            .Bind(configuration.GetSection(JwtSettings.SectionName))
            .Validate(s => !string.IsNullOrWhiteSpace(s.Issuer), "JwtSettings:Issuer is required.")
            .Validate(s => !string.IsNullOrWhiteSpace(s.Audience), "JwtSettings:Audience is required.")
            .Validate(s => !string.IsNullOrWhiteSpace(s.Key) && s.Key.Length >= 32,
                "JwtSettings:Key is required and must be at least 32 characters.")
            .Validate(s => !string.Equals(
                s.Key, "REPLACE_WITH_A_LONG_RANDOM_SECRET_AT_LEAST_32_CHARS", StringComparison.Ordinal),
                "JwtSettings:Key is still set to the appsettings placeholder. " +
                "Override via environment variable JwtSettings__Key or user-secrets before starting the API.")
            .Validate(s => s.AccessTokenMinutes > 0, "JwtSettings:AccessTokenMinutes must be > 0.")
            .Validate(s => s.RefreshTokenDays > 0, "JwtSettings:RefreshTokenDays must be > 0.")
            .ValidateOnStart();

        var jwt = configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>()
                  ?? new JwtSettings();

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.RequireHttpsMetadata = false;
            options.SaveToken = true;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwt.Issuer,
                ValidAudience = jwt.Audience,
                IssuerSigningKey = string.IsNullOrWhiteSpace(jwt.Key)
                    ? null
                    : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
                ClockSkew = TimeSpan.Zero
            };
        });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthorizationPolicies.RequireAdmin, p =>
                p.RequireAuthenticatedUser()
                 .RequireRole(SystemRoles.Admin));

            options.AddPolicy(AuthorizationPolicies.RequireCustomer, p =>
                p.RequireAuthenticatedUser()
                 .RequireRole(SystemRoles.Customer));

            // Go-live alignment — technician portal scope. Admins
            // also satisfy this so they can use technician endpoints
            // in dev/diagnostics without juggling extra accounts.
            options.AddPolicy(AuthorizationPolicies.RequireTechnician, p =>
                p.RequireAuthenticatedUser()
                 .RequireRole(SystemRoles.Technician, SystemRoles.Admin, SystemRoles.SuperAdmin));

            options.AddPolicy(AuthorizationPolicies.RequireActiveUser, p =>
                p.RequireAuthenticatedUser()
                 .RequireClaim(
                    AuthorizationPolicies.AccountStatusClaim,
                    AuthorizationPolicies.ActiveAccountStatus));
        });

        return services;
    }

    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
        return services;
    }

    // Coverage check: options + named HttpClients + provider wiring.
    // Geocoding (Google Maps) is optional; the implementation
    // self-detects a missing key and returns PROVIDER_NOT_CONFIGURED
    // so callers that supply lat/lon directly keep working.
    public static IServiceCollection AddCoverageServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<CoverageSettings>()
            .Bind(configuration.GetSection(CoverageSettings.SectionName));

        var coverageSettings = configuration.GetSection(CoverageSettings.SectionName)
            .Get<CoverageSettings>() ?? new CoverageSettings();

        services.AddHttpClient(OpenserveFibreCoverageProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(coverageSettings.Openserve.BaseUrl);
            client.Timeout     = TimeSpan.FromSeconds(Math.Clamp(coverageSettings.Openserve.TimeoutSeconds, 1, 30));
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        });

        services.AddHttpClient(GoogleGeocodingService.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://maps.googleapis.com");
            client.Timeout     = TimeSpan.FromSeconds(Math.Clamp(coverageSettings.Openserve.TimeoutSeconds, 1, 30));
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        });

        services.AddScoped<IGeocodingService, GoogleGeocodingService>();
        // Server-side Google Places proxy (autocomplete + place details).
        // Reuses the GoogleGeocoding HttpClient + the same API key —
        // single key, single Google Cloud Console restriction set.
        // Mobile devices without Google Play Services (Huawei) call
        // these endpoints instead of hitting maps.googleapis.com directly.
        services.AddScoped<IGooglePlacesService, GooglePlacesService>();
        services.AddScoped<IFibreCoverageProvider, OpenserveFibreCoverageProvider>();

        return services;
    }

    public static IServiceCollection AddAuthServices(this IServiceCollection services)
    {
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IPortalAuthHandoffService, PortalAuthHandoffService>();
        services.AddScoped<ICustomerProfileService, CustomerProfileService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IServicePackageService, ServicePackageService>();
        services.AddScoped<ICoverageRequestService, CoverageRequestService>();
        services.AddScoped<ICoverageCheckService, CoverageCheckService>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IOrderIntentService, OrderIntentService>();
        services.AddScoped<IInstallationService, InstallationService>();
        services.AddScoped<IInvoiceService, InvoiceService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IDebitOrderMandateService, DebitOrderMandateService>();
        services.AddScoped<IBillingOverviewService, BillingOverviewService>();
        services.AddScoped<ISupportTicketService, SupportTicketService>();
        services.AddScoped<INotificationService, NotificationService>();
        // INotificationSender is registered by AddEmailServices below so the
        // implementation can be chosen from configuration at startup.
        services.AddScoped<IWebhookInboxService, WebhookInboxService>();
        services.AddScoped<PermissiveWebhookSignatureValidator>();
        services.AddScoped<IWebhookSignatureValidator, CompositeWebhookSignatureValidator>();
        services.AddScoped<IWebhookPayloadParser, BasicJsonWebhookPayloadParser>();
        services.AddScoped<IAdminSystemService, AdminSystemService>();
        services.AddScoped<IAdminReportService, AdminReportService>();
        services.AddScoped<IAdminDashboardService, AdminDashboardService>();
        services.AddScoped<IAdminCustomerService, AdminCustomerService>();
        services.AddScoped<IAdminUsersService, AdminUsersService>();
        services.AddScoped<IPrivacyRequestService, PrivacyRequestService>();

        // Payment gateway foundation
        services.AddScoped<IPaymentApplierService, PaymentApplierService>();
        services.AddScoped<IPaymentGatewayService, PaymentGatewayService>();
        services.AddScoped<IPaymentProviderRegistry, PaymentProviderRegistry>();
        services.AddScoped<IPaymentInitiator, ManualPaymentInitiator>();

        // Phase 52 — Ozow. Registered as another IPaymentInitiator so
        // PaymentProviderRegistry picks it up automatically. HttpClient
        // is named so the integration test harness can stub it.
        services.AddHttpClient<OzowPaymentInitiator>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<IPaymentInitiator>(sp => sp.GetRequiredService<OzowPaymentInitiator>());
        services.AddScoped<OzowNotifyHandler>();

        // PayFast — same IPaymentInitiator pattern. No HttpClient needed
        // because PayFast uses form-POST redirect, not a server-to-server
        // API call. The initiator builds the redirect URL with signed params.
        services.AddScoped<PayFastPaymentInitiator>();
        services.AddScoped<IPaymentInitiator>(sp => sp.GetRequiredService<PayFastPaymentInitiator>());
        services.AddScoped<PayFastNotifyHandler>();

        // Paystack — primary payment gateway. Server-to-server initialize
        // call needs an HttpClient (typed); same client is reused by the
        // verification service. The notify handler is webhook-driven
        // (no HttpClient of its own). The PaymentProviderRegistry
        // auto-discovers PaystackPaymentInitiator via the IPaymentInitiator
        // collection registration.
        services.AddHttpClient<PaystackPaymentInitiator>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<IPaymentInitiator>(sp => sp.GetRequiredService<PaystackPaymentInitiator>());
        services.AddHttpClient<PaystackVerificationService>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddScoped<PaystackNotifyHandler>();
        services.AddScoped<IPaystackReconciliationService, PaystackReconciliationService>();
        // Read-only status lookup for mobile / portal polling. No Paystack
        // call — just reads our DB rows so the result screen can poll
        // every few seconds without spending Paystack quota.
        services.AddScoped<IPaystackStatusService, PaystackStatusService>();
        services.AddScoped<IPaystackWebhookLogQueryService, PaystackWebhookLogQueryService>();
        // Phase 53 — intent-bound Paystack initiator. Used by the
        // new "create-intent-and-pay" client checkout endpoint.
        services.AddHttpClient<IPaystackIntentInitiationService, PaystackIntentInitiationService>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
        });

        // Phase 2/3 — reusable-mandate storage. Protected at rest by
        // ASP.NET Core DataProtection (added in AddCommunicationProviders
        // alongside PaymentProcessingSettings + AutoBillingSettings).
        services.AddScoped<IMandateProtector, DataProtectionMandateProtector>();
        services.AddScoped<ICustomerPaymentMandateService, CustomerPaymentMandateService>();

        // Phase 7 — charge-authorization implementation. Behaviour gated by
        // AutoBilling__ChargeAuthorizationEnabled (default: false).
        services.AddHttpClient<PaystackChargeAuthorizationService>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
        });

        // Phase 4 — orchestration seam for every auto-charge entry-point
        // (install hook, future retry job, future monthly job).
        services.AddScoped<IAutoBillingService, AutoBillingService>();
        services.AddScoped<AutoBillingEmailService>();

        // Network provisioning foundation
        services.AddScoped<INetworkAccountService, NetworkAccountService>();
        // Admin Client Service Detail "Activate Service / Force Settle"
        // orchestrator. Composes NetworkAccountService + AutoBillingService
        // + OrderService — lives on its own seam to avoid a DI cycle
        // (PaymentApplierService → NetworkAccountService).
        services.AddScoped<IAdminClientServiceActionsService, AdminClientServiceActionsService>();
        services.AddScoped<INetworkProvisioner, LoggingNetworkProvisioner>();
        services.AddScoped<INetworkProvisioningService, NoOpNetworkProvisioningService>();
        services.AddScoped<IRadiusProfileService, RadiusProfileService>();
        services.AddScoped<IProvisioningEventService, ProvisioningEventService>();

        // Phase 51 — customer-initiated upgrade / downgrade workflow.
        services.AddScoped<IServiceChangeRequestService, ServiceChangeRequestService>();

        services.AddMemoryCache();
        return services;
    }

    public static IServiceCollection AddEmailServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Options bindings are kept so the older providers (and the
        // multi-sender pool) still compile if anyone needs to swap
        // them back in. They are NOT used by the active
        // INotificationSender registration below.
        services.AddOptions<EmailSettings>().Bind(configuration.GetSection(EmailSettings.SectionName));
        services.AddOptions<EmailProvidersSettings>()
            .Bind(configuration.GetSection(EmailProvidersSettings.SectionName));
        services.AddOptions<EmailTestModeSettings>()
            .Bind(configuration.GetSection(EmailTestModeSettings.SectionName));

        // Concrete senders stay registered as themselves so manual
        // resolution + diagnostics tooling can still see them.
        // **They do NOT register against `INotificationSender`.**
        services.AddScoped<LoggingNotificationSender>();
        services.AddScoped<SmtpEmailSender>();
        services.AddScoped<SmtpMultiSenderEmailSender>();
        services.AddScoped<TestModeSmtpNotificationSender>();

        // ─── ACTIVE PROVIDER ───────────────────────────────────────────────
        //
        // Per-category outbound mail via SmtpMultiSenderEmailSender, bound
        // to the `EmailProviders` section. Each request's SenderType
        // (Security / Accounts / Payments / Support / NoReply) resolves
        // to its own From-address + SMTP credentials on
        // notify.smartfuture.co.za (SmarterASP). Missing-sender requests
        // fall back to `EmailProviders:DefaultSender` (NoReply) with a
        // warning logged — see SmtpMultiSenderEmailSender.ResolveSenders.
        //
        // Passwords are NEVER committed: each sender's Password is read
        // from env vars:
        //   EmailProviders__Senders__NoReply__Password
        //   EmailProviders__Senders__Support__Password
        //   EmailProviders__Senders__Accounts__Password
        //   EmailProviders__Senders__Payments__Password
        //   EmailProviders__Senders__Security__Password
        //
        // Test-mode single-mailbox sender remains registered as itself so
        // ops can manually swap it back in via this line if a SmarterASP
        // outage forces a fallback, but it is no longer the
        // INotificationSender. EmailSettings (legacy single-sender) +
        // EmailTestMode also stay bound for the same reason.
        services.AddScoped<INotificationSender, SmtpMultiSenderEmailSender>();

        return services;
    }

    /// <summary>
    /// Communication providers — SMS, WhatsApp, phone verification.
    /// Wires the NotConfigured stubs by default; real Twilio
    /// implementations are deferred to a follow-up phase. Always-on
    /// `TwilioSettings` binding lets future code branch on
    /// <see cref="TwilioSettings.IsConfigured"/> without having to
    /// re-thread configuration.
    /// </summary>
    public static IServiceCollection AddCommunicationProviders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<TwilioSettings>()
            .Bind(configuration.GetSection(TwilioSettings.SectionName));

        // Phase 3.6 — provisioning kill-switch + mode. Bound here
        // because this is the only DI extension that already takes
        // IConfiguration; the registration is logically independent
        // of the Twilio block above.
        services.AddOptions<ProvisioningSettings>()
            .Bind(configuration.GetSection(ProvisioningSettings.SectionName));

        // Paystack recurring-billing rollout — Phase 1/4/7 feature flags.
        // Defaults are safe (WebhookApply on, AutoBilling off).
        services.AddOptions<PaymentProcessingSettings>()
            .Bind(configuration.GetSection(PaymentProcessingSettings.SectionName));
        services.AddOptions<AutoBillingSettings>()
            .Bind(configuration.GetSection(AutoBillingSettings.SectionName));

        // ASP.NET Core DataProtection — used by DataProtectionMandateProtector
        // to encrypt stored Paystack authorization codes. Default key
        // store (OS-managed) is fine for single-instance hosting; for
        // multi-instance / EAS-style deploys, point this at an Azure
        // Key Vault or persisted file share in a follow-up. SetApplicationName
        // pins the protector purpose chain so a rename can't silently
        // invalidate stored mandates.
        services.AddDataProtection()
            .SetApplicationName("SmartFuture.API");

        services.AddScoped<ISmsProvider, NotConfiguredSmsProvider>();
        services.AddScoped<IWhatsAppProvider, NotConfiguredWhatsAppProvider>();

        // Twilio Verify: use real implementation when ServiceSid is
        // configured, otherwise fall back to the stub that returns
        // PROVIDER_NOT_CONFIGURED.
        var twilioSection = configuration.GetSection(TwilioSettings.SectionName);
        var verifySid = twilioSection.GetSection("Verify")["ServiceSid"];
        var accountSid = twilioSection["AccountSid"];
        if (!string.IsNullOrWhiteSpace(verifySid) && !string.IsNullOrWhiteSpace(accountSid))
        {
            services.AddScoped<IPhoneVerificationService, TwilioVerifyService>();
        }
        else
        {
            services.AddScoped<IPhoneVerificationService, NotConfiguredPhoneVerificationService>();
        }

        return services;
    }

    public static IServiceCollection AddSmartFutureBackgroundServices(this IServiceCollection services)
    {
        services.AddHostedService<ExpiredRefreshTokenCleanupHostedService>();
        // Logs the registered `INotificationSender` concrete type once
        // at startup so operators can confirm the multi-sender SMTP
        // path is wired up after a config change.
        services.AddHostedService<EmailSenderStartupLogger>();
        return services;
    }

    public static async Task SeedDatabaseAsync(this IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("StartupSeeding");

        try
        {
            await DbInitializer.SeedAsync(serviceProvider);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Database seeding failed. This is expected when the database does not yet exist " +
                "(e.g., during 'dotnet ef migrations add' or before 'database update' has been run). " +
                "Startup will continue.");
        }
    }

    public static IServiceCollection AddApiServices(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        return services;
    }

    public static IServiceCollection AddCustomCors(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                      ?? Array.Empty<string>();

        // Non-production environments (Development / UAT / Staging) also
        // accept any http://localhost:* and http://127.0.0.1:* origin so
        // tooling like Expo Web / Vite / CRA dev servers can call the API
        // without having every port pre-listed in config. The check is
        // gated on `!IsProduction()` so Live never auto-allows localhost.
        var allowLocalhost = !environment.IsProduction();

        services.AddCors(options =>
        {
            options.AddPolicy(FrontendCorsPolicy, policy =>
            {
                if (origins.Length == 0 && !allowLocalhost)
                {
                    // Empty explicit list AND we're in production — fall
                    // back to allow-any so a misconfigured deployment
                    // doesn't lock everyone out. Same behaviour as the
                    // pre-Phase 52 implementation.
                    policy.SetIsOriginAllowed(_ => true);
                }
                else if (origins.Length == 0)
                {
                    // Non-production + no explicit list — allow any
                    // localhost loopback origin (including Expo Web).
                    policy.SetIsOriginAllowed(IsLocalhostOrigin);
                }
                else
                {
                    // Explicit list — permit the configured origins
                    // verbatim and, when non-production, also allow any
                    // localhost loopback so devs don't need to update
                    // config for every port.
                    policy.SetIsOriginAllowed(origin =>
                        origins.Any(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase))
                        || (allowLocalhost && IsLocalhostOrigin(origin)));
                }

                policy.AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials();
            });
        });

        return services;
    }

    /// <summary>
    /// Match http(s)://localhost[:port] and http(s)://127.0.0.1[:port].
    /// Conservative on purpose — only the loopback host names, only via
    /// HTTP(S), no wildcard subdomains.
    /// </summary>
    private static bool IsLocalhostOrigin(string origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.Ordinal);
    }

    public static WebApplication ConfigureMiddleware(this WebApplication app)
    {
        app.UseForwardedHeaders();

        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.UseMiddleware<SecurityHeadersMiddleware>();

        // Swagger: enabled in Development by default; off everywhere else unless
        // explicitly opted in via Swagger:Enabled = true (e.g., to expose docs on UAT).
        var swaggerEnabled = app.Configuration.GetValue<bool?>("Swagger:Enabled")
                             ?? app.Environment.IsDevelopment();
        if (swaggerEnabled)
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();

        app.UseCors(FrontendCorsPolicy);

        app.UseRateLimiter();

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapSmartFutureHealthChecks();
        app.MapControllers();

        return app;
    }
}

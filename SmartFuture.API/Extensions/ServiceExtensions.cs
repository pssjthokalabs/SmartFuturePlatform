using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using SmartFuture.API.Configuration;
using SmartFuture.API.HostedServices;
using SmartFuture.API.Middleware;
using SmartFuture.API.Services;
using SmartFuture.Application.Admin;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auth;
using SmartFuture.Application.Billing;
// PaymentSettings lives in SmartFuture.Application.Billing — same namespace as above.
using SmartFuture.Application.Common.Interfaces.Identity;
using SmartFuture.Application.Common.Interfaces.Shared;
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
using SmartFuture.Application.Payments;
using SmartFuture.Application.Privacy;
using SmartFuture.Application.Reports;
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

        services.AddOptions<PaymentSettings>()
            .Bind(configuration.GetSection(PaymentSettings.SectionName));

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

    public static IServiceCollection AddAuthServices(this IServiceCollection services)
    {
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IPortalAuthHandoffService, PortalAuthHandoffService>();
        services.AddScoped<ICustomerProfileService, CustomerProfileService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IServicePackageService, ServicePackageService>();
        services.AddScoped<ICoverageRequestService, CoverageRequestService>();
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

        // Network provisioning foundation
        services.AddScoped<INetworkAccountService, NetworkAccountService>();
        services.AddScoped<INetworkProvisioner, LoggingNetworkProvisioner>();

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

        // ─── HARD WIRE (Phase 35D-fix) ─────────────────────────────────────
        //
        // INotificationSender is forced to TestModeSmtpNotificationSender.
        // No provider switch, no factory, no MultiSmtp routing. Use
        // `EmailTestMode:*` config to point it at whichever SMTP mailbox
        // you want — when those settings are blank the sender returns a
        // FailedResult with the missing-field name, never silently
        // logs-and-succeeds. Revert this single line to restore the
        // Phase 35D factory.
        services.AddScoped<INotificationSender, TestModeSmtpNotificationSender>();

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

        // Stubs only for now — replaced by Twilio implementations when
        // those land. Stubs are safe to register unconditionally:
        // they return PROVIDER_NOT_CONFIGURED, never throw.
        services.AddScoped<ISmsProvider, NotConfiguredSmsProvider>();
        services.AddScoped<IWhatsAppProvider, NotConfiguredWhatsAppProvider>();
        services.AddScoped<IPhoneVerificationService, NotConfiguredPhoneVerificationService>();

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

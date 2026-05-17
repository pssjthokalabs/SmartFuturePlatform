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
using SmartFuture.Application.Common.Interfaces.Identity;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.CoverageRequests;
using SmartFuture.Application.CustomerProfiles;
using SmartFuture.Application.Customers.Admin;
using SmartFuture.Application.Dashboard;
using SmartFuture.Application.Installations;
using SmartFuture.Application.NetworkAccounts;
using SmartFuture.Application.Notifications;
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
        services.AddScoped<ICustomerProfileService, CustomerProfileService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IServicePackageService, ServicePackageService>();
        services.AddScoped<ICoverageRequestService, CoverageRequestService>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IInstallationService, InstallationService>();
        services.AddScoped<IInvoiceService, InvoiceService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IDebitOrderMandateService, DebitOrderMandateService>();
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
        var section = configuration.GetSection(EmailSettings.SectionName);
        services.AddOptions<EmailSettings>().Bind(section);

        var provider = section.GetValue<string>(nameof(EmailSettings.Provider));
        if (string.Equals(provider, "Smtp", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<INotificationSender, SmtpEmailSender>();
        }
        else
        {
            // Default to the logging sender for local dev, demos, and any
            // environment where SMTP isn't intentionally turned on. This
            // keeps OutboundNotifications producing rows even when no real
            // mailer is wired up.
            services.AddScoped<INotificationSender, LoggingNotificationSender>();
        }

        return services;
    }

    public static IServiceCollection AddSmartFutureBackgroundServices(this IServiceCollection services)
    {
        services.AddHostedService<ExpiredRefreshTokenCleanupHostedService>();
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
        IConfiguration configuration)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                      ?? Array.Empty<string>();

        services.AddCors(options =>
        {
            options.AddPolicy(FrontendCorsPolicy, policy =>
            {
                if (origins.Length == 0)
                {
                    policy.SetIsOriginAllowed(_ => true);
                }
                else
                {
                    policy.WithOrigins(origins);
                }

                policy.AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials();
            });
        });

        return services;
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

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFuture.Domain.Identity;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Infrastructure.Configuration;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Identity;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Infrastructure.Data.Seeding;

public static class DbInitializer
{
    public static async Task SeedAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;

        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("DbInitializer");

        var roleManager = sp.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        await SeedRolesAsync(roleManager, logger);

        var userManager = sp.GetRequiredService<UserManager<User>>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        await SeedSuperAdminAsync(userManager, roleManager, configuration, logger);

        var dbContext = sp.GetRequiredService<AppDbContext>();
        await SeedServicePackagesAsync(dbContext, configuration, logger);
    }

    private static async Task SeedRolesAsync(
        RoleManager<IdentityRole<Guid>> roleManager,
        ILogger logger)
    {
        foreach (var roleName in SystemRoles.All)
        {
            if (await roleManager.RoleExistsAsync(roleName))
                continue;

            var role = new IdentityRole<Guid>(roleName)
            {
                Id = Guid.NewGuid(),
                NormalizedName = roleName.ToUpperInvariant()
            };

            var result = await roleManager.CreateAsync(role);
            if (result.Succeeded)
            {
                logger.LogInformation("Seeded role {Role}", roleName);
            }
            else
            {
                var errors = string.Join("; ", result.Errors.Select(e => e.Description));
                logger.LogError("Failed to seed role {Role}: {Errors}", roleName, errors);
            }
        }
    }

    private static async Task SeedSuperAdminAsync(
        UserManager<User> userManager,
        RoleManager<IdentityRole<Guid>> roleManager,
        IConfiguration configuration,
        ILogger logger)
    {
        var settings = configuration.GetSection(SeedSuperAdminSettings.SectionName)
            .Get<SeedSuperAdminSettings>() ?? new SeedSuperAdminSettings();

        if (!settings.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(settings.Email))
        {
            logger.LogWarning("SeedSuperAdmin is enabled but Email is empty — skipping.");
            return;
        }

        var email = settings.Email.Trim();
        var existing = await userManager.FindByEmailAsync(email);

        if (existing is null)
        {
            if (string.IsNullOrWhiteSpace(settings.Password))
            {
                logger.LogWarning(
                    "SeedSuperAdmin is enabled but Password is empty — cannot create new user {Email}. " +
                    "Set SeedSuperAdmin__Password via environment variables.",
                    email);
                return;
            }

            var user = new User
            {
                UserName = email,
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                NormalizedUserName = email.ToUpperInvariant(),
                EmailConfirmed = true,
                PhoneNumber = string.IsNullOrWhiteSpace(settings.PhoneNumber) ? null : settings.PhoneNumber.Trim(),
                PhoneNumberConfirmed = !string.IsNullOrWhiteSpace(settings.PhoneNumber),
                FirstName = settings.FirstName.Trim(),
                LastName = settings.LastName.Trim(),
                AccountStatus = UserAccountStatus.Active,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow
            };

            var createResult = await userManager.CreateAsync(user, settings.Password);
            if (!createResult.Succeeded)
            {
                var errors = string.Join("; ", createResult.Errors.Select(e => e.Description));
                logger.LogError("Failed to create super admin {Email}: {Errors}", email, errors);
                return;
            }

            logger.LogInformation("Seeded super admin user {Email}", email);
            await EnsureSuperAdminRolesAsync(userManager, user, logger);
            return;
        }

        // User exists — never reset the password. Just make sure the
        // SuperAdmin + Admin roles are assigned so the seeded account
        // can sign in to the admin portal even on a re-deploy.
        await EnsureSuperAdminRolesAsync(userManager, existing, logger);
    }

    private static async Task EnsureSuperAdminRolesAsync(
        UserManager<User> userManager,
        User user,
        ILogger logger)
    {
        foreach (var role in new[] { SystemRoles.SuperAdmin, SystemRoles.Admin })
        {
            if (await userManager.IsInRoleAsync(user, role))
                continue;

            var result = await userManager.AddToRoleAsync(user, role);
            if (result.Succeeded)
                logger.LogInformation("Assigned role {Role} to {Email}", role, user.Email);
            else
            {
                var errors = string.Join("; ", result.Errors.Select(e => e.Description));
                logger.LogError("Failed to assign role {Role} to {Email}: {Errors}", role, user.Email, errors);
            }
        }
    }

    private static async Task SeedServicePackagesAsync(
        AppDbContext dbContext,
        IConfiguration configuration,
        ILogger logger)
    {
        var settings = configuration.GetSection(PackageSeedSettings.SectionName)
            .Get<PackageSeedSettings>() ?? new PackageSeedSettings();

        if (!settings.Enabled)
            return;

        var seeds = OpenserveFibreSeeds();
        var changed = 0;

        foreach (var seed in seeds)
        {
            var existing = await dbContext.ServicePackages
                .FirstOrDefaultAsync(p => p.ExternalReference == seed.ExternalReference);

            if (existing is null)
            {
                dbContext.ServicePackages.Add(seed);
                changed++;
                logger.LogInformation("Seeded package {Ref} ({Name})", seed.ExternalReference, seed.Name);
                continue;
            }

            if (!settings.UpdateExisting)
                continue;

            existing.Name = seed.Name;
            existing.Type = seed.Type;
            existing.Status = seed.Status;
            existing.Description = seed.Description;
            existing.ShortDescription = seed.ShortDescription;
            existing.SpeedLabel = seed.SpeedLabel;
            existing.DownloadSpeedMbps = seed.DownloadSpeedMbps;
            existing.UploadSpeedMbps = seed.UploadSpeedMbps;
            existing.DataAllowanceLabel = seed.DataAllowanceLabel;
            existing.IsUncapped = seed.IsUncapped;
            existing.Price = seed.Price;
            existing.BillingCycle = seed.BillingCycle;
            existing.ContractMonths = seed.ContractMonths;
            existing.HasFreeInstallation = seed.HasFreeInstallation;
            existing.InstallationFee = seed.InstallationFee;
            existing.IncludesRouter = seed.IncludesRouter;
            existing.RouterDescription = seed.RouterDescription;
            existing.TermsSummary = seed.TermsSummary;
            existing.DisplayOrder = seed.DisplayOrder;
            changed++;
            logger.LogInformation("Updated seeded package {Ref}", seed.ExternalReference);
        }

        if (changed > 0)
            await dbContext.SaveChangesAsync();
    }

    private static IReadOnlyList<ServicePackage> OpenserveFibreSeeds()
    {
        // Source: Openserve fibre price sheet, Smart Future, May 2026.
        // The 300/150 Mbps row on the source sheet reads R243.00pm,
        // which is well below the surrounding 100/100 (R920) and
        // 500/250 (R1399) rows and almost certainly a typo. We
        // deliberately skip seeding that row — admins can add it
        // manually through the portal once pricing is confirmed.
        var specs = new (int Down, int Up, decimal Price)[]
        {
            (20,  10,  370m),
            (40,  25,  570m),
            (50,  25,  685m),
            (50,  50,  770m),
            (100, 50,  740m),
            (100, 100, 920m),
            (200, 100, 1080m),
            (200, 200, 1120m),
            // (300, 150, 243m) — suspected typo, skipped on purpose
            (500, 250, 1399m)
        };

        var list = new List<ServicePackage>(specs.Length);
        var displayOrder = 0;
        foreach (var (down, up, price) in specs)
        {
            list.Add(new ServicePackage
            {
                Type = ServicePackageType.Fibre,
                Status = ServicePackageStatus.Active,
                Name = $"Openserve Fibre {down}/{up} Mbps",
                Description = "Openserve fibre — uncapped data, WiFi router included, month-to-month.",
                ShortDescription = "Openserve fibre",
                SpeedLabel = $"{down}/{up} Mbps",
                DownloadSpeedMbps = down,
                UploadSpeedMbps = up,
                DataAllowanceLabel = "Unlimited",
                IsUncapped = true,
                Price = price,
                BillingCycle = ServicePackageBillingCycle.Monthly,
                ContractMonths = null, // month-to-month
                HasFreeInstallation = false,
                InstallationFee = 100m,
                IncludesRouter = true,
                RouterDescription = "WiFi router included",
                IsFeatured = false,
                DisplayOrder = displayOrder++,
                TermsSummary = "Month-to-month, no fixed contract.",
                ExternalReference = $"openserve-fibre-{down}-{up}"
            });
        }
        return list;
    }
}

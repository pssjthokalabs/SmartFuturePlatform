using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Users;
using SmartFuture.Domain.Identity;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Infrastructure.Configuration;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Identity;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Utilities;

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
        await BackfillUserNumbersAsync(dbContext, logger);
        await BackfillPhoneNumberNormalizedAsync(dbContext, logger);
        await SeedServicePackagesAsync(dbContext, configuration, logger);
        await SeedRadiusProfilesAsync(dbContext, logger);
    }

    private static async Task SeedRadiusProfilesAsync(AppDbContext dbContext, ILogger logger)
    {
        var seeds = new[]
        {
            ("25M/25M", 25, 25, 1),
            ("50M/50M", 50, 50, 2),
            ("100M/100M", 100, 100, 3),
            ("200M/200M", 200, 200, 4),
            ("500M/500M", 500, 500, 5),
            ("1G/1G", 1000, 1000, 6)
        };

        var changed = 0;
        foreach (var (name, down, up, priority) in seeds)
        {
            var existing = await dbContext.RadiusProfiles.FirstOrDefaultAsync(p => p.Name == name);
            if (existing is not null) continue;

            dbContext.RadiusProfiles.Add(new RadiusProfile
            {
                Id = Guid.NewGuid(), Name = name,
                DownloadMbps = down, UploadMbps = up,
                Priority = priority, IsActive = true,
                CreatedAtUtc = DateTime.UtcNow
            });
            changed++;
            logger.LogInformation("Seeded RadiusProfile {Name} ({Down}/{Up} Mbps)", name, down, up);
        }

        if (changed > 0) await dbContext.SaveChangesAsync();
    }

    // Phase 43 — backfill canonical phone numbers on existing users.
    // Robust to legacy duplicates: when two rows normalize to the same
    // value we only set it on the earliest row and leave the rest null
    // so the unique-filtered index can be created without conflict.
    // Admins can then resolve the duplicates from the Users page.
    private static async Task BackfillPhoneNumberNormalizedAsync(
        AppDbContext dbContext,
        ILogger logger)
    {
        var candidates = await dbContext.Users
            .Where(u => u.PhoneNumberNormalized == null && u.PhoneNumber != null)
            .OrderBy(u => u.CreatedAtUtc).ThenBy(u => u.Id)
            .ToListAsync();

        if (candidates.Count == 0) return;

        // Seed the seen-set with already-normalized values so we don't
        // re-allocate a slot a different row already claimed.
        var taken = await dbContext.Users
            .Where(u => u.PhoneNumberNormalized != null)
            .Select(u => u.PhoneNumberNormalized!)
            .ToListAsync();
        var seen = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);

        var applied = 0;
        var skippedDupes = 0;

        foreach (var row in candidates)
        {
            var canonical = PhoneNumberNormalizer.Normalize(row.PhoneNumber);
            if (canonical is null) continue;

            if (seen.Add(canonical))
            {
                row.PhoneNumberNormalized = canonical;
                applied++;
            }
            else
            {
                skippedDupes++;
            }
        }

        if (applied > 0) await dbContext.SaveChangesAsync();

        logger.LogInformation(
            "Backfilled PhoneNumberNormalized on {Applied} users.{SkippedHint}",
            applied,
            skippedDupes > 0
                ? $" Skipped {skippedDupes} duplicates (left null; resolve from the Users page)."
                : string.Empty);
    }

    // Phase 41 — backfill any existing users that don't yet have a
    // UserNumber. Runs after roles + super-admin seeding so the seeded
    // SuperAdmin gets the first slot deterministically when it's the
    // only existing row.
    private static async Task BackfillUserNumbersAsync(
        AppDbContext dbContext,
        ILogger logger)
    {
        var rows = await dbContext.Users
            .Where(u => u.UserNumber == null)
            .OrderBy(u => u.CreatedAtUtc).ThenBy(u => u.Id)
            .ToListAsync();

        if (rows.Count == 0) return;

        var max = await dbContext.Users.MaxAsync(u => (int?)u.UserNumber)
                  ?? (UserNumberAllocator.MinUserNumber - 1);
        var next = Math.Max(UserNumberAllocator.MinUserNumber, max + 1);

        foreach (var u in rows)
        {
            u.UserNumber = next++;
        }

        await dbContext.SaveChangesAsync();
        logger.LogInformation(
            "Backfilled UserNumber for {Count} users (assigned {FirstNumber}-{LastNumber}).",
            rows.Count, rows[0].UserNumber, rows[^1].UserNumber);
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
                // UserNumber is intentionally null here — the BackfillUserNumbers
                // pass at the end of SeedAsync assigns the next available slot
                // deterministically, including for the SuperAdmin row.
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

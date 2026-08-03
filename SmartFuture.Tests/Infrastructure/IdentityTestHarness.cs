using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFuture.Domain.Identity;
using SmartFuture.Infrastructure.Data;
using SmartFuture.Shared.Constants;

namespace SmartFuture.Tests.Infrastructure;

/// <summary>
/// Real ASP.NET Core Identity (UserManager / SignInManager / RoleManager)
/// wired to the SQLite test database.
///
/// Why real Identity rather than mocks: the behaviour under test IS
/// Identity behaviour — role membership, password verification, and the
/// unique-email constraint. Mocking <c>IsInRoleAsync</c> would make the
/// role-upgrade tests assert on the mock instead of on what actually
/// happens to a user row, which is the exact regression we care about
/// (a job subscriber signing up for fibre must not be blocked, and must
/// not lose the role they already had).
/// </summary>
public sealed class IdentityTestHarness : IAsyncDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly IServiceScope _scope;

    private IdentityTestHarness(SqliteTestDbFixture fixture, ServiceProvider serviceProvider, IServiceScope scope)
    {
        Fixture = fixture;
        _serviceProvider = serviceProvider;
        _scope = scope;

        UserManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        SignInManager = scope.ServiceProvider.GetRequiredService<SignInManager<User>>();
        RoleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
    }

    public SqliteTestDbFixture Fixture { get; }
    public AppDbContext DbContext => Fixture.DbContext;
    public UserManager<User> UserManager { get; }
    public SignInManager<User> SignInManager { get; }
    public RoleManager<IdentityRole<Guid>> RoleManager { get; }

    public static async Task<IdentityTestHarness> CreateAsync()
    {
        var fixture = await SqliteTestDbFixture.CreateAsync();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        // Hand Identity the SAME context instance the tests assert
        // against, so a role written through UserManager is immediately
        // visible to a direct DbContext query.
        services.AddSingleton(fixture.DbContext);
        services.AddSingleton<DbContext>(fixture.DbContext);

        services.AddIdentityCore<User>(options =>
            {
                // Mirror the production policy from ServiceExtensions so
                // a password accepted here would be accepted live.
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredLength = 8;
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedEmail = false;
                options.SignIn.RequireConfirmedPhoneNumber = false;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        // SignInManager needs these two even when we only ever call
        // CheckPasswordSignInAsync (no cookie is ever issued).
        services.AddHttpContextAccessor();
        services.AddAuthentication();

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();

        var harness = new IdentityTestHarness(fixture, provider, scope);
        await harness.SeedRolesAsync();
        return harness;
    }

    private async Task SeedRolesAsync()
    {
        foreach (var role in SystemRoles.All)
        {
            if (await RoleManager.RoleExistsAsync(role)) continue;
            await RoleManager.CreateAsync(new IdentityRole<Guid>(role)
            {
                Id = Guid.NewGuid(),
                NormalizedName = role.ToUpperInvariant()
            });
        }
    }

    /// <summary>
    /// Creates an active user with the supplied roles and password.
    /// </summary>
    public async Task<User> CreateUserAsync(string email, string password, params string[] roles)
    {
        var user = new User
        {
            UserName = email,
            Email = email,
            FirstName = "Test",
            LastName = "User",
            IsActive = true,
            EmailConfirmed = true,
            CreatedAtUtc = DateTime.UtcNow
        };

        var created = await UserManager.CreateAsync(user, password);
        if (!created.Succeeded)
            throw new InvalidOperationException($"Test user creation failed: {string.Join("; ", created.Errors.Select(e => e.Description))}");

        foreach (var role in roles)
        {
            var added = await UserManager.AddToRoleAsync(user, role);
            if (!added.Succeeded)
                throw new InvalidOperationException($"Test role assignment failed: {string.Join("; ", added.Errors.Select(e => e.Description))}");
        }

        return user;
    }

    public async Task<IList<string>> GetRolesAsync(Guid userId)
    {
        var user = await UserManager.FindByIdAsync(userId.ToString())
            ?? throw new InvalidOperationException($"User {userId} not found.");
        return await UserManager.GetRolesAsync(user);
    }

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _serviceProvider.DisposeAsync();
        await Fixture.DisposeAsync();
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SmartFuture.Application.Persistence;
using SmartFuture.Infrastructure.Data;

namespace SmartFuture.Tests.Infrastructure;

/// <summary>
/// SQLite in-memory harness for billing DB integration tests.
///
/// Why SQLite + not EF InMemory:
///   • EF InMemory has no relational engine — unique indexes, filtered
///     unique indexes, decimal precision, and joins all differ from
///     SQL Server in ways that can silently pass tests that would fail
///     on production. SQLite is a real relational engine; it still isn't
///     100% SQL Server, but the invariants we care about (duplicate
///     detection via unique index, join projections in
///     RecurringInvoiceGenerator / GraceSuspensionRunner, decimal 18,2
///     rounding) all behave correctly.
///
/// Why in-memory + <c>Open()</c>:
///   • Using <c>Filename=:memory:</c> alone destroys the DB the moment
///     the connection closes. Keeping ONE open connection alive across
///     the whole context lifetime keeps the schema in place; the
///     <see cref="DisposeAsync"/> method closes it after the test.
///
/// Schema strategy:
///   • <c>EnsureCreatedAsync</c> reads the existing configurations under
///     <c>SmartFuture.Infrastructure/Data/Configurations</c> and materialises
///     them straight to SQLite. We deliberately skip the SQL Server
///     migration history — migrations use SQL Server-only DDL (T-SQL
///     helpers) that SQLite would reject.
///   • Column types that are SQL Server-only (<c>nvarchar(max)</c>,
///     <c>varchar(max)</c>) are STRIPPED from the model at first-boot
///     time — see <see cref="SqliteAppDbContext.OnModelCreating"/>. On
///     SQL Server they force MAX; on SQLite we let EF's default relational
///     type mapping pick TEXT. This mirrors production behaviour for
///     tests without leaking test hacks into the real DbContext.
///
/// Determinism:
///   • This fixture never calls <c>DateTime.UtcNow</c>. Tests hand each
///     entity concrete UTC dates and pass a fixed <c>NowUtc</c> into any
///     <c>RecurringBillingRunContext</c> they build, so results don't
///     drift with the wall clock.
/// </summary>
public sealed class SqliteTestDbFixture : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private SqliteTestDbFixture(SqliteConnection connection, SqliteAppDbContext dbContext)
    {
        _connection = connection;
        DbContext = dbContext;
    }

    /// <summary>
    /// The live <see cref="AppDbContext"/> — a test-only subclass that
    /// rewrites SQL Server-only column types for SQLite compatibility.
    /// </summary>
    public AppDbContext DbContext { get; }

    /// <summary>
    /// Same context typed as <see cref="IAppDbContext"/> — convenience
    /// for services that take the interface via DI.
    /// </summary>
    public IAppDbContext AppDbContext => DbContext;

    public static async Task<SqliteTestDbFixture> CreateAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .EnableSensitiveDataLogging()
            .Options;

        var dbContext = new SqliteAppDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        return new SqliteTestDbFixture(connection, dbContext);
    }

    /// <summary>
    /// A brand-new context on the SAME in-memory database — no shared
    /// change-tracker state. Lets a test model "a different process reading
    /// the same database" (e.g. the API after an IIS recycle). Caller disposes.
    /// </summary>
    public AppDbContext CreateSiblingContext()
        => new SqliteAppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).EnableSensitiveDataLogging().Options);

    public async ValueTask DisposeAsync()
    {
        await DbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    /// <summary>
    /// Test-only DbContext subclass that walks the built model after the
    /// production configurations run and strips SQL Server-only column
    /// types EF's SQLite provider can't translate. Production code uses
    /// <see cref="AppDbContext"/> directly — this override is confined
    /// to the test assembly.
    /// </summary>
    public sealed class SqliteAppDbContext : AppDbContext
    {
        public SqliteAppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // Drop SQL Server-specific column type annotations so the
            // SQLite type mapper falls back to its default TEXT mapping.
            // We do NOT touch max length limits — those still get
            // enforced by EF's validation on save.
            foreach (var entity in builder.Model.GetEntityTypes())
            {
                foreach (var property in entity.GetProperties())
                {
                    var columnType = property.GetColumnType();
                    if (string.IsNullOrEmpty(columnType)) continue;

                    var normalized = columnType.ToLowerInvariant();
                    if (normalized == "nvarchar(max)"
                        || normalized == "varchar(max)"
                        || normalized == "nvarchar(max) collate ai")
                    {
                        property.SetColumnType(null);
                    }
                }

                // Strip the SQL Server rowversion concurrency token.
                // SQLite doesn't auto-increment BLOB columns, so an
                // UPDATE that carries the tracked entity's original
                // rowversion value hits a concurrency mismatch on every
                // save. Production still uses the token via SQL Server;
                // this override is confined to the test assembly and
                // ONLY affects entities loaded via the test fixture.
                var rowVersion = entity.FindProperty("RowVersion");
                if (rowVersion is not null)
                {
                    rowVersion.IsConcurrencyToken = false;
                    rowVersion.ValueGenerated = ValueGenerated.Never;
                }
            }
        }
    }
}

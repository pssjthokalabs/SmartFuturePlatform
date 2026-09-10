using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    // Data-only migration (brief Priority 10 / §19): the client's
    // complaint is that daily crawling only ever happens when an admin
    // presses Refresh. JobImportHostedService already runs a scheduled
    // loop honouring each source's own CrawlFrequencyMinutes — sources
    // just never had a value set, so none were ever "due". This backfill
    // makes every currently-active, non-Manual, not-yet-scheduled source
    // eligible for once-daily automatic crawling (1440 minutes) without
    // touching any source an admin already gave a deliberate frequency.
    // Automatic crawling still only actually runs once an admin also
    // enables JobModuleSettings.AutoImportEnabled (Job Settings page) —
    // this migration only makes eligible sources reachable by the
    // scheduler once that switch is on.
    /// <inheritdoc />
    public partial class SeedDailyCrawlFrequencyForActiveJobSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE [JobSources] SET [CrawlFrequencyMinutes] = 1440 " +
                "WHERE [CrawlFrequencyMinutes] IS NULL AND [IsActive] = 1 AND [SourceType] <> 3;");
            // SourceType 3 = Manual (SmartFuture.Shared.Enums.Jobs.JobSourceType.Manual)
            // — hand-curated sources have nothing to crawl and must stay
            // untouched regardless of this backfill.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately no-op: reversing a data backfill by blanket-
            // nulling CrawlFrequencyMinutes again would also wipe out any
            // legitimate value an admin set (via the normal update
            // endpoint) AFTER this migration ran, which Down cannot tell
            // apart from the seeded rows.
        }
    }
}

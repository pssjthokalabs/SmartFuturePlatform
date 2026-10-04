using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenserveSubmissionRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OpenserveAutomationPauseReason",
                table: "Orders",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OpenserveAutomationPaused",
                table: "Orders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "OpenserveAutomationPausedAtUtc",
                table: "Orders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OpenserveAutomationPausedByUserId",
                table: "Orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AutomaticRetryCount",
                table: "OpenserveOrders",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LastFailureClass",
                table: "OpenserveOrders",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSubmissionAttemptAtUtc",
                table: "OpenserveOrders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastSubmissionTrigger",
                table: "OpenserveOrders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAutomaticRetryAtUtc",
                table: "OpenserveOrders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SubmissionTrigger",
                table: "OpenserveIntegrationLogs",
                type: "int",
                nullable: true);

            // Backfill: classify Failed rows written before LastFailureClass
            // existed, from each row's latest Create Order log — same rules as
            // OpenserveSubmissionFailureClassifier. Never sent (no HTTP method)
            // = Blocked (3); timeout / dropped connection / unreadable answer /
            // 5xx / no status = OutcomeUnknown (4); 408/429/503 = Retryable (1);
            // anything else = NonRetryable (2). NextAutomaticRetryAtUtc stays
            // NULL, so no legacy row is ever resent automatically after deploy.
            // EXEC keeps the idempotent deploy script compilable.
            migrationBuilder.Sql(@"EXEC(N'
UPDATE o SET o.LastFailureClass = CASE
    WHEN lastLog.HttpMethod IS NULL THEN 3
    WHEN o.LastFailureCode IN (N''TIMEOUT'', N''TRANSPORT_ERROR'', N''PARSE_ERROR'') THEN 4
    WHEN lastLog.ResponseStatusCode IN (408, 429, 503) THEN 1
    WHEN lastLog.ResponseStatusCode IS NULL OR lastLog.ResponseStatusCode >= 500 THEN 4
    ELSE 2 END
FROM OpenserveOrders o
OUTER APPLY (SELECT TOP 1 l.HttpMethod, l.ResponseStatusCode FROM OpenserveIntegrationLogs l
             WHERE l.OpenserveOrderId = o.Id AND l.OperationType = 1 ORDER BY l.OccurredAtUtc DESC) lastLog
WHERE o.NormalizedStatus = 7 AND o.LastFailureClass = 0;

UPDATE OpenserveOrders SET LastFailureCode = CASE
    WHEN LastFailureMessage LIKE N''%mapping%'' THEN N''BLOCKED_MAPPING''
    WHEN LastFailureMessage LIKE N''%AMID%'' THEN N''BLOCKED_AMID''
    WHEN LastFailureMessage LIKE N''%street address%'' THEN N''BLOCKED_ADDRESS''
    ELSE N''BLOCKED_CONTACT'' END
WHERE NormalizedStatus = 7 AND LastFailureClass = 3 AND LastFailureCode IS NULL;
')");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveOrders_NextAutomaticRetryAtUtc",
                table: "OpenserveOrders",
                column: "NextAutomaticRetryAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OpenserveOrders_NextAutomaticRetryAtUtc",
                table: "OpenserveOrders");

            migrationBuilder.DropColumn(
                name: "OpenserveAutomationPauseReason",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OpenserveAutomationPaused",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OpenserveAutomationPausedAtUtc",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OpenserveAutomationPausedByUserId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "AutomaticRetryCount",
                table: "OpenserveOrders");

            migrationBuilder.DropColumn(
                name: "LastFailureClass",
                table: "OpenserveOrders");

            migrationBuilder.DropColumn(
                name: "LastSubmissionAttemptAtUtc",
                table: "OpenserveOrders");

            migrationBuilder.DropColumn(
                name: "LastSubmissionTrigger",
                table: "OpenserveOrders");

            migrationBuilder.DropColumn(
                name: "NextAutomaticRetryAtUtc",
                table: "OpenserveOrders");

            migrationBuilder.DropColumn(
                name: "SubmissionTrigger",
                table: "OpenserveIntegrationLogs");
        }
    }
}

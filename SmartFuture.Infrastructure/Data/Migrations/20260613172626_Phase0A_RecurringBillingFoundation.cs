using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase0A_RecurringBillingFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PeriodEndUtc",
                table: "Invoices",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PeriodStartUtc",
                table: "Invoices",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ServiceBillingScheduleId",
                table: "Invoices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BillingRunLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    TriggeredBy = table.Column<int>(type: "int", nullable: false),
                    DryRun = table.Column<bool>(type: "bit", nullable: false),
                    InvoicesGenerated = table.Column<int>(type: "int", nullable: false),
                    ChargesAttempted = table.Column<int>(type: "int", nullable: false),
                    ChargesSucceeded = table.Column<int>(type: "int", nullable: false),
                    ChargesFailed = table.Column<int>(type: "int", nullable: false),
                    RetriesProcessed = table.Column<int>(type: "int", nullable: false),
                    SuspensionCandidates = table.Column<int>(type: "int", nullable: false),
                    ErrorCount = table.Column<int>(type: "int", nullable: false),
                    MachineName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    InstanceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SummaryJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ErrorText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingRunLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServiceBillingSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NetworkAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BillingCycle = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    AnchorDayOfMonth = table.Column<int>(type: "int", nullable: false),
                    CurrentPeriodStartUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CurrentPeriodEndUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextInvoiceDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextDueDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastInvoicedPeriodEndUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastInvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IsAutoBillable = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceBillingSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceBillingSchedules_NetworkAccounts_NetworkAccountId",
                        column: x => x.NetworkAccountId,
                        principalTable: "NetworkAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_ServiceBillingScheduleId",
                table: "Invoices",
                column: "ServiceBillingScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_BillingRunLogs_StartedAtUtc",
                table: "BillingRunLogs",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BillingRunLogs_Status_StartedAtUtc",
                table: "BillingRunLogs",
                columns: new[] { "Status", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceBillingSchedules_NetworkAccountId",
                table: "ServiceBillingSchedules",
                column: "NetworkAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceBillingSchedules_OrderId",
                table: "ServiceBillingSchedules",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceBillingSchedules_Status_NextInvoiceDateUtc",
                table: "ServiceBillingSchedules",
                columns: new[] { "Status", "NextInvoiceDateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceBillingSchedules_UserId",
                table: "ServiceBillingSchedules",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_ServiceBillingSchedules_ServiceBillingScheduleId",
                table: "Invoices",
                column: "ServiceBillingScheduleId",
                principalTable: "ServiceBillingSchedules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_ServiceBillingSchedules_ServiceBillingScheduleId",
                table: "Invoices");

            migrationBuilder.DropTable(
                name: "BillingRunLogs");

            migrationBuilder.DropTable(
                name: "ServiceBillingSchedules");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_ServiceBillingScheduleId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "PeriodEndUtc",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "PeriodStartUtc",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ServiceBillingScheduleId",
                table: "Invoices");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase0B_RecurringInvoiceUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Invoices_ServiceBillingScheduleId_PeriodStartUtc",
                table: "Invoices",
                columns: new[] { "ServiceBillingScheduleId", "PeriodStartUtc" },
                unique: true,
                filter: "[ServiceBillingScheduleId] IS NOT NULL AND [PeriodStartUtc] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoices_ServiceBillingScheduleId_PeriodStartUtc",
                table: "Invoices");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPaystackWebhookLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaystackWebhookLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RawBodyLength = table.Column<int>(type: "int", nullable: false),
                    SignaturePresent = table.Column<bool>(type: "bit", nullable: false),
                    SignatureValid = table.Column<bool>(type: "bit", nullable: false),
                    Event = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AmountSubunits = table.Column<long>(type: "bigint", nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    PaymentInitiationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Accepted = table.Column<bool>(type: "bit", nullable: false),
                    OutcomeMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RejectionReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ApplyAttempted = table.Column<bool>(type: "bit", nullable: false),
                    ApplySucceeded = table.Column<bool>(type: "bit", nullable: false),
                    ApplyErrorCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    ApplyErrorMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HttpStatusReturned = table.Column<int>(type: "int", nullable: false),
                    EnvironmentName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaystackWebhookLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaystackWebhookLogs_ReceivedAtUtc",
                table: "PaystackWebhookLogs",
                column: "ReceivedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PaystackWebhookLogs_Reference",
                table: "PaystackWebhookLogs",
                column: "Reference");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaystackWebhookLogs");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebhookInboxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    ProviderName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ProviderEventId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EventType = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RawPayloadHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    SignatureHeader = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    PaymentReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    InvoiceReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OrderReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    GatewayTransactionId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    ProviderCreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ParsedSummaryJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookInboxes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebhookInboxes_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WebhookInboxes_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInboxes_CreatedAtUtc",
                table: "WebhookInboxes",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInboxes_EventType",
                table: "WebhookInboxes",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInboxes_GatewayTransactionId",
                table: "WebhookInboxes",
                column: "GatewayTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInboxes_IdempotencyKey",
                table: "WebhookInboxes",
                column: "IdempotencyKey");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInboxes_InvoiceId",
                table: "WebhookInboxes",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInboxes_InvoiceReference",
                table: "WebhookInboxes",
                column: "InvoiceReference");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInboxes_OrderReference",
                table: "WebhookInboxes",
                column: "OrderReference");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInboxes_PaymentId",
                table: "WebhookInboxes",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInboxes_PaymentReference",
                table: "WebhookInboxes",
                column: "PaymentReference");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInboxes_ProcessedAtUtc",
                table: "WebhookInboxes",
                column: "ProcessedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInboxes_Provider",
                table: "WebhookInboxes",
                column: "Provider");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInboxes_ProviderEventId",
                table: "WebhookInboxes",
                column: "ProviderEventId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInboxes_ProviderName",
                table: "WebhookInboxes",
                column: "ProviderName");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInboxes_ProviderName_IdempotencyKey",
                table: "WebhookInboxes",
                columns: new[] { "ProviderName", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInboxes_ProviderName_ProviderEventId",
                table: "WebhookInboxes",
                columns: new[] { "ProviderName", "ProviderEventId" },
                unique: true,
                filter: "[ProviderEventId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInboxes_RawPayloadHash",
                table: "WebhookInboxes",
                column: "RawPayloadHash");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInboxes_Status",
                table: "WebhookInboxes",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebhookInboxes");
        }
    }
}

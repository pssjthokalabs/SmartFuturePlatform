using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPaystackAutoBillingFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Phase 1 — per-initiation dry-run switch and webhook
            // receipt timestamp. Both default-safe so existing rows
            // keep behaving exactly as they did before the upgrade.
            migrationBuilder.AddColumn<int>(
                name: "WebhookApplyMode",
                table: "PaymentInitiations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "WebhookLastReceivedAtUtc",
                table: "PaymentInitiations",
                type: "datetime2",
                nullable: true);

            // Phase 3 — customer-controlled opt-out for the auto-debit
            // job. Defaults to false; customers must explicitly opt in.
            migrationBuilder.AddColumn<bool>(
                name: "AutoBillingEnabled",
                table: "CustomerProfiles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Phase 2 — reusable payment authorizations captured from
            // a successful gateway charge (Paystack today).
            migrationBuilder.CreateTable(
                name: "CustomerPaymentMandates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    ProviderCustomerCode = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AuthorizationCodeProtected = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    AuthorizationSignature = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Channel = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    CardType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Bank = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Last4 = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: true),
                    ExpMonth = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    ExpYear = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: true),
                    AccountName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CustomerEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    IsReusable = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    ConsentGivenUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConsentRevokedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConsentSource = table.Column<int>(type: "int", nullable: false),
                    LastSuccessfulChargeUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastFailedChargeUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConsecutiveFailureCount = table.Column<int>(type: "int", nullable: false),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerPaymentMandates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerPaymentMandates_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentMandates_UserId",
                table: "CustomerPaymentMandates",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentMandates_UserId_Provider",
                table: "CustomerPaymentMandates",
                columns: new[] { "UserId", "Provider" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentMandates_UserId_Provider_AuthorizationSignature",
                table: "CustomerPaymentMandates",
                columns: new[] { "UserId", "Provider", "AuthorizationSignature" },
                unique: true,
                filter: "[AuthorizationSignature] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentMandates_IsActive",
                table: "CustomerPaymentMandates",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentMandates_IsDefault",
                table: "CustomerPaymentMandates",
                column: "IsDefault");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "CustomerPaymentMandates");

            migrationBuilder.DropColumn(
                name: "AutoBillingEnabled",
                table: "CustomerProfiles");

            migrationBuilder.DropColumn(
                name: "WebhookApplyMode",
                table: "PaymentInitiations");

            migrationBuilder.DropColumn(
                name: "WebhookLastReceivedAtUtc",
                table: "PaymentInitiations");
        }
    }
}

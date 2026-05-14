using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentInitiations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaymentInitiations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    ProviderReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ProviderCheckoutId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RedirectUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SuccessUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CancelUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    FailureUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentInitiations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentInitiations_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentInitiations_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentInitiations_CreatedAtUtc",
                table: "PaymentInitiations",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentInitiations_ExpiresAtUtc",
                table: "PaymentInitiations",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentInitiations_InvoiceId",
                table: "PaymentInitiations",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentInitiations_PaymentId",
                table: "PaymentInitiations",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentInitiations_Provider",
                table: "PaymentInitiations",
                column: "Provider");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentInitiations_ProviderCheckoutId",
                table: "PaymentInitiations",
                column: "ProviderCheckoutId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentInitiations_ProviderReference",
                table: "PaymentInitiations",
                column: "ProviderReference");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentInitiations_Status",
                table: "PaymentInitiations",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentInitiations");
        }
    }
}

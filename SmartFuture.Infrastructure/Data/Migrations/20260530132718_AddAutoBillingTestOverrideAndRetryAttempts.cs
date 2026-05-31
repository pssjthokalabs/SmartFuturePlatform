using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoBillingTestOverrideAndRetryAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ActualProviderAmount",
                table: "Payments",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "InvoiceAmountAtTime",
                table: "Payments",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsTestAmountOverrideApplied",
                table: "Payments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TestOverrideReason",
                table: "Payments",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ActualProviderAmount",
                table: "PaymentInitiations",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "InvoiceAmountAtTime",
                table: "PaymentInitiations",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsTestAmountOverrideApplied",
                table: "PaymentInitiations",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PaymentRetryAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MandateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ProviderAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ProviderReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Source = table.Column<int>(type: "int", nullable: false),
                    ScheduledForUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AttemptedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentRetryAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentRetryAttempts_AspNetUsers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentRetryAttempts_CustomerPaymentMandates_MandateId",
                        column: x => x.MandateId,
                        principalTable: "CustomerPaymentMandates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PaymentRetryAttempts_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentRetryAttempts_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRetryAttempts_CustomerId",
                table: "PaymentRetryAttempts",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRetryAttempts_InvoiceId",
                table: "PaymentRetryAttempts",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRetryAttempts_MandateId",
                table: "PaymentRetryAttempts",
                column: "MandateId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRetryAttempts_PaymentId",
                table: "PaymentRetryAttempts",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRetryAttempts_ProviderReference",
                table: "PaymentRetryAttempts",
                column: "ProviderReference");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRetryAttempts_Status_ScheduledForUtc",
                table: "PaymentRetryAttempts",
                columns: new[] { "Status", "ScheduledForUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentRetryAttempts");

            migrationBuilder.DropColumn(
                name: "ActualProviderAmount",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "InvoiceAmountAtTime",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "IsTestAmountOverrideApplied",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "TestOverrideReason",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ActualProviderAmount",
                table: "PaymentInitiations");

            migrationBuilder.DropColumn(
                name: "InvoiceAmountAtTime",
                table: "PaymentInitiations");

            migrationBuilder.DropColumn(
                name: "IsTestAmountOverrideApplied",
                table: "PaymentInitiations");
        }
    }
}

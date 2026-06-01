using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderIntentPaystackFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "IntentInvoiceAmountAtTime",
                table: "OrderIntents",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IntentIsTestAmountOverrideApplied",
                table: "OrderIntents",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "IntentPaymentAccessCode",
                table: "OrderIntents",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "IntentPaymentAmount",
                table: "OrderIntents",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IntentPaymentInitiatedAtUtc",
                table: "OrderIntents",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntentPaymentRedirectUrl",
                table: "OrderIntents",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntentPaymentReference",
                table: "OrderIntents",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderIntents_IntentPaymentReference",
                table: "OrderIntents",
                column: "IntentPaymentReference",
                unique: true,
                filter: "[IntentPaymentReference] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OrderIntents_IntentPaymentReference",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "IntentInvoiceAmountAtTime",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "IntentIsTestAmountOverrideApplied",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "IntentPaymentAccessCode",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "IntentPaymentAmount",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "IntentPaymentInitiatedAtUtc",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "IntentPaymentRedirectUrl",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "IntentPaymentReference",
                table: "OrderIntents");
        }
    }
}

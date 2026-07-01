using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingDayOptionsAndServicePackageFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FeaturesJson",
                table: "ServicePackages",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FirstProRataInvoiceGeneratedAtUtc",
                table: "Orders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreferredBillingDay",
                table: "Orders",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PreferredBillingDay",
                table: "OrderIntents",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BillingDayOptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<int>(type: "int", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingDayOptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BillingDayOptions_Day",
                table: "BillingDayOptions",
                column: "Day",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingDayOptions_DisplayOrder",
                table: "BillingDayOptions",
                column: "DisplayOrder");

            migrationBuilder.CreateIndex(
                name: "IX_BillingDayOptions_IsEnabled",
                table: "BillingDayOptions",
                column: "IsEnabled");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingDayOptions");

            migrationBuilder.DropColumn(
                name: "FeaturesJson",
                table: "ServicePackages");

            migrationBuilder.DropColumn(
                name: "FirstProRataInvoiceGeneratedAtUtc",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PreferredBillingDay",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PreferredBillingDay",
                table: "OrderIntents");
        }
    }
}

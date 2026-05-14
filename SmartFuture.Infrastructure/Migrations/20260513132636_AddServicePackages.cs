using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddServicePackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ServicePackages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ShortDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SpeedLabel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DownloadSpeedMbps = table.Column<int>(type: "int", nullable: true),
                    UploadSpeedMbps = table.Column<int>(type: "int", nullable: true),
                    DataAllowanceLabel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsUncapped = table.Column<bool>(type: "bit", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    BillingCycle = table.Column<int>(type: "int", nullable: false),
                    ContractMonths = table.Column<int>(type: "int", nullable: true),
                    HasFreeInstallation = table.Column<bool>(type: "bit", nullable: false),
                    InstallationFee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    IncludesRouter = table.Column<bool>(type: "bit", nullable: false),
                    RouterDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsFeatured = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    TermsSummary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CoverageNotes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ExternalReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServicePackages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServicePackages_CreatedAtUtc",
                table: "ServicePackages",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePackages_DisplayOrder",
                table: "ServicePackages",
                column: "DisplayOrder");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePackages_ExternalReference",
                table: "ServicePackages",
                column: "ExternalReference",
                unique: true,
                filter: "[ExternalReference] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePackages_IsFeatured",
                table: "ServicePackages",
                column: "IsFeatured");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePackages_Name",
                table: "ServicePackages",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePackages_Price",
                table: "ServicePackages",
                column: "Price");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePackages_Status",
                table: "ServicePackages",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePackages_Type",
                table: "ServicePackages",
                column: "Type");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServicePackages");
        }
    }
}

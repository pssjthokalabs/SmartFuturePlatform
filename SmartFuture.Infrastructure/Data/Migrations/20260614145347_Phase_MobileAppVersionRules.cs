using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase_MobileAppVersionRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MobileAppVersionRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Platform = table.Column<int>(type: "int", nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    LatestVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    LatestBuildNumber = table.Column<int>(type: "int", nullable: false),
                    MinimumSupportedVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    MinimumSupportedBuildNumber = table.Column<int>(type: "int", nullable: false),
                    UpdateRequired = table.Column<bool>(type: "bit", nullable: false),
                    UpdateAvailable = table.Column<bool>(type: "bit", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                    PrimaryButtonText = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SecondaryButtonText = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    StoreUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ReleaseNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MobileAppVersionRules", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "MobileAppVersionRules",
                columns: new[] { "Id", "Channel", "CreatedAtUtc", "IsEnabled", "LatestBuildNumber", "LatestVersion", "Message", "MinimumSupportedBuildNumber", "MinimumSupportedVersion", "Platform", "PrimaryButtonText", "ReleaseNotes", "SecondaryButtonText", "StoreUrl", "Title", "UpdateAvailable", "UpdateRequired", "UpdatedAtUtc", "UpdatedByUserId" },
                values: new object[,]
                {
                    { new Guid("a1111111-1111-1111-1111-111111111111"), 0, new DateTime(2026, 6, 14, 0, 0, 0, 0, DateTimeKind.Utc), true, 0, "1.0.1", "A new version of SmartFuture is available with improvements and fixes.", 0, "1.0.0", 0, "Update app", null, "Later", "https://play.google.com/store/apps/details?id=com.smartfuture.app", "Update available", false, false, null, null },
                    { new Guid("a2222222-2222-2222-2222-222222222222"), 1, new DateTime(2026, 6, 14, 0, 0, 0, 0, DateTimeKind.Utc), true, 0, "1.0.1", "A new version of SmartFuture is available with improvements and fixes.", 0, "1.0.0", 0, "Update app", null, "Later", "https://appgallery.huawei.com/app/C118015685", "Update available", false, false, null, null },
                    { new Guid("a3333333-3333-3333-3333-333333333333"), 2, new DateTime(2026, 6, 14, 0, 0, 0, 0, DateTimeKind.Utc), true, 0, "1.0.1", "A new version of SmartFuture is available with improvements and fixes.", 0, "1.0.0", 1, "Update app", null, "Later", "", "Update available", false, false, null, null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_MobileAppVersionRules_Platform_Channel",
                table: "MobileAppVersionRules",
                columns: new[] { "Platform", "Channel" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MobileAppVersionRules");
        }
    }
}

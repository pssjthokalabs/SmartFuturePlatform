using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobDetailsMobileAppOnlyAndStoreLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppleAppStoreUrl",
                table: "JobModuleSettings",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GooglePlayUrl",
                table: "JobModuleSettings",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HuaweiAppGalleryUrl",
                table: "JobModuleSettings",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "JobDetailsMobileAppOnly",
                table: "JobModuleSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppleAppStoreUrl",
                table: "JobModuleSettings");

            migrationBuilder.DropColumn(
                name: "GooglePlayUrl",
                table: "JobModuleSettings");

            migrationBuilder.DropColumn(
                name: "HuaweiAppGalleryUrl",
                table: "JobModuleSettings");

            migrationBuilder.DropColumn(
                name: "JobDetailsMobileAppOnly",
                table: "JobModuleSettings");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityServiceTypeAndImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImageStorageKey",
                table: "ServicePackages",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageUrl",
                table: "ServicePackages",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImageStorageKey",
                table: "ServicePackages");

            migrationBuilder.DropColumn(
                name: "ImageUrl",
                table: "ServicePackages");
        }
    }
}

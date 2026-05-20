using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPhoneNumberNormalized : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PhoneNumberNormalized",
                table: "AspNetUsers",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_PhoneNumberNormalized",
                table: "AspNetUsers",
                column: "PhoneNumberNormalized",
                unique: true,
                filter: "[PhoneNumberNormalized] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_PhoneNumberNormalized",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PhoneNumberNormalized",
                table: "AspNetUsers");
        }
    }
}

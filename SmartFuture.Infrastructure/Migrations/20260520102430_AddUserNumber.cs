using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UserNumber",
                table: "AspNetUsers",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_UserNumber",
                table: "AspNetUsers",
                column: "UserNumber",
                unique: true,
                filter: "[UserNumber] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_UserNumber",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "UserNumber",
                table: "AspNetUsers");
        }
    }
}

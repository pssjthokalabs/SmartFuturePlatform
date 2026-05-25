using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixCoverageRequestUserDeleteBehavior : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CoverageRequests_AspNetUsers_UserId",
                table: "CoverageRequests");

            migrationBuilder.AddForeignKey(
                name: "FK_CoverageRequests_AspNetUsers_UserId",
                table: "CoverageRequests",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CoverageRequests_AspNetUsers_UserId",
                table: "CoverageRequests");

            migrationBuilder.AddForeignKey(
                name: "FK_CoverageRequests_AspNetUsers_UserId",
                table: "CoverageRequests",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}

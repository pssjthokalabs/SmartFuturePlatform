using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MakeCoverageRequestUserIdNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CoverageRequests_AspNetUsers_UserId",
                table: "CoverageRequests");

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                table: "CoverageRequests",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddForeignKey(
                name: "FK_CoverageRequests_AspNetUsers_UserId",
                table: "CoverageRequests",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CoverageRequests_AspNetUsers_UserId",
                table: "CoverageRequests");

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                table: "CoverageRequests",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CoverageRequests_AspNetUsers_UserId",
                table: "CoverageRequests",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}

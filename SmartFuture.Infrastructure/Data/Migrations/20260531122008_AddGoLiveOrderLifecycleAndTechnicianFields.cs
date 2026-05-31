using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGoLiveOrderLifecycleAndTechnicianFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ActivatedByUserId",
                table: "Orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActivationNotes",
                table: "Orders",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BillingAnchorDateUtc",
                table: "Orders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextPayDateUtc",
                table: "Orders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OpenserveActivationReference",
                table: "Orders",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerSignOffName",
                table: "Installations",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InstalledLocationNotes",
                table: "Installations",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OntReference",
                table: "Installations",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RouterMacAddress",
                table: "Installations",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RouterMakeModel",
                table: "Installations",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RouterSerialNumber",
                table: "Installations",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpeedTestResult",
                table: "Installations",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ActivatedByUserId",
                table: "Orders",
                column: "ActivatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_NextPayDateUtc",
                table: "Orders",
                column: "NextPayDateUtc");

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_AspNetUsers_ActivatedByUserId",
                table: "Orders",
                column: "ActivatedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_AspNetUsers_ActivatedByUserId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_ActivatedByUserId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_NextPayDateUtc",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ActivatedByUserId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ActivationNotes",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "BillingAnchorDateUtc",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "NextPayDateUtc",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OpenserveActivationReference",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CustomerSignOffName",
                table: "Installations");

            migrationBuilder.DropColumn(
                name: "InstalledLocationNotes",
                table: "Installations");

            migrationBuilder.DropColumn(
                name: "OntReference",
                table: "Installations");

            migrationBuilder.DropColumn(
                name: "RouterMacAddress",
                table: "Installations");

            migrationBuilder.DropColumn(
                name: "RouterMakeModel",
                table: "Installations");

            migrationBuilder.DropColumn(
                name: "RouterSerialNumber",
                table: "Installations");

            migrationBuilder.DropColumn(
                name: "SpeedTestResult",
                table: "Installations");
        }
    }
}

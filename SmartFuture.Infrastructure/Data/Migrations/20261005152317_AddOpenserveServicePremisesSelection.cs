using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenserveServicePremisesSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OpenservePremisesAddress",
                table: "Orders",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OpenservePremisesCustomerConfirmedAtUtc",
                table: "Orders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OpenservePremisesDistanceMeters",
                table: "Orders",
                type: "decimal(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OpenservePremisesSelectedAtUtc",
                table: "Orders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OpenservePremisesSelectedByUserId",
                table: "Orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OpenservePremisesSelection",
                table: "Orders",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OpenservePremisesAddress",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OpenservePremisesCustomerConfirmedAtUtc",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OpenservePremisesDistanceMeters",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OpenservePremisesSelectedAtUtc",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OpenservePremisesSelectedByUserId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OpenservePremisesSelection",
                table: "Orders");
        }
    }
}

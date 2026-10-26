using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderFulfilmentRoute : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FulfilmentCoverageRuleId",
                table: "Orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FulfilmentRoute",
                table: "Orders",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "FulfilmentCoverageRuleId",
                table: "OrderIntents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FulfilmentRoute",
                table: "OrderIntents",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FulfilmentCoverageRuleId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "FulfilmentRoute",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "FulfilmentCoverageRuleId",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "FulfilmentRoute",
                table: "OrderIntents");
        }
    }
}

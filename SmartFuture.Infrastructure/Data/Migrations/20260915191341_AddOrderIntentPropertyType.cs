using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderIntentPropertyType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BuildingComplexName",
                table: "OrderIntents",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PropertyType",
                table: "OrderIntents",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnitNumber",
                table: "OrderIntents",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BuildingComplexName",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "PropertyType",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "UnitNumber",
                table: "OrderIntents");
        }
    }
}

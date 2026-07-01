using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServicePackageSubTypesAndVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SubTypeId",
                table: "ServicePackages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PackageVariantName",
                table: "Orders",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ServicePackageVariantId",
                table: "Orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ServicePackageVariantId",
                table: "OrderIntents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ServicePackageSubTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PackageType = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServicePackageSubTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServicePackageVariants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServicePackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    InstallationFee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    HasFreeInstallation = table.Column<bool>(type: "bit", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServicePackageVariants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServicePackageVariants_ServicePackages_ServicePackageId",
                        column: x => x.ServicePackageId,
                        principalTable: "ServicePackages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "ServicePackageSubTypes",
                columns: new[] { "Id", "CreatedAtUtc", "DisplayOrder", "IsActive", "Name", "PackageType", "Slug", "UpdatedAtUtc" },
                values: new object[,]
                {
                    { new Guid("5b1c0001-0000-4000-8000-0000000000c1"), new DateTime(2026, 7, 1, 0, 0, 0, 0, DateTimeKind.Utc), 10, true, "CCTV", 6, "cctv", null },
                    { new Guid("5b1c0002-0000-4000-8000-0000000000c2"), new DateTime(2026, 7, 1, 0, 0, 0, 0, DateTimeKind.Utc), 20, true, "Intercom", 6, "intercom", null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServicePackages_SubTypeId",
                table: "ServicePackages",
                column: "SubTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ServicePackageVariantId",
                table: "Orders",
                column: "ServicePackageVariantId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderIntents_ServicePackageVariantId",
                table: "OrderIntents",
                column: "ServicePackageVariantId");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePackageSubTypes_DisplayOrder",
                table: "ServicePackageSubTypes",
                column: "DisplayOrder");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePackageSubTypes_IsActive",
                table: "ServicePackageSubTypes",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePackageSubTypes_PackageType",
                table: "ServicePackageSubTypes",
                column: "PackageType");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePackageSubTypes_PackageType_Slug",
                table: "ServicePackageSubTypes",
                columns: new[] { "PackageType", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServicePackageVariants_DisplayOrder",
                table: "ServicePackageVariants",
                column: "DisplayOrder");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePackageVariants_IsActive",
                table: "ServicePackageVariants",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePackageVariants_ServicePackageId",
                table: "ServicePackageVariants",
                column: "ServicePackageId");

            migrationBuilder.AddForeignKey(
                name: "FK_OrderIntents_ServicePackageVariants_ServicePackageVariantId",
                table: "OrderIntents",
                column: "ServicePackageVariantId",
                principalTable: "ServicePackageVariants",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_ServicePackageVariants_ServicePackageVariantId",
                table: "Orders",
                column: "ServicePackageVariantId",
                principalTable: "ServicePackageVariants",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ServicePackages_ServicePackageSubTypes_SubTypeId",
                table: "ServicePackages",
                column: "SubTypeId",
                principalTable: "ServicePackageSubTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OrderIntents_ServicePackageVariants_ServicePackageVariantId",
                table: "OrderIntents");

            migrationBuilder.DropForeignKey(
                name: "FK_Orders_ServicePackageVariants_ServicePackageVariantId",
                table: "Orders");

            migrationBuilder.DropForeignKey(
                name: "FK_ServicePackages_ServicePackageSubTypes_SubTypeId",
                table: "ServicePackages");

            migrationBuilder.DropTable(
                name: "ServicePackageSubTypes");

            migrationBuilder.DropTable(
                name: "ServicePackageVariants");

            migrationBuilder.DropIndex(
                name: "IX_ServicePackages_SubTypeId",
                table: "ServicePackages");

            migrationBuilder.DropIndex(
                name: "IX_Orders_ServicePackageVariantId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_OrderIntents_ServicePackageVariantId",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "SubTypeId",
                table: "ServicePackages");

            migrationBuilder.DropColumn(
                name: "PackageVariantName",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ServicePackageVariantId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ServicePackageVariantId",
                table: "OrderIntents");
        }
    }
}

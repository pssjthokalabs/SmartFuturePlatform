using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenserveQualificationEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OpenserveQualificationResultId",
                table: "Orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OpenserveQualificationResultId",
                table: "OrderIntents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OpenserveQualificationResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Purpose = table.Column<int>(type: "int", nullable: false),
                    IntegrationLogId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    QualifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    QueryLatitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    QueryLongitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    QueryAmid = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    CallSucceeded = table.Column<bool>(type: "bit", nullable: false),
                    HttpStatusCode = table.Column<int>(type: "int", nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AddressIdentified = table.Column<bool>(type: "bit", nullable: false),
                    Amid = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    CanonicalAddress = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    StreetNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    StreetName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StreetType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Suburb = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Town = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Province = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Region = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Latitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    Longitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    AddressStatus = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DistanceMeters = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    DistanceText = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    MduVerification = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AddressMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    BuildingCandidateCount = table.Column<int>(type: "int", nullable: false),
                    BuildingCandidatesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FibreAvailability = table.Column<int>(type: "int", nullable: false),
                    FtthStatusSummary = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    FtthInfrastructureCount = table.Column<int>(type: "int", nullable: false),
                    FibreMaxSpeedMbps = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    AvailableProductCodes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    EthernetProductCodes = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    FwaStatus = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CustomerAddress = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: true),
                    AddressMatch = table.Column<int>(type: "int", nullable: false),
                    AddressMatchDetail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AddressAcceptedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AddressAcceptedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AddressAcceptanceNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ServicePackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MappingSku = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    MappingCapacity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    MappingCapacityUom = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ProductEligibility = table.Column<int>(type: "int", nullable: false),
                    EligibilityReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenserveQualificationResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenserveQualificationProducts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QualificationResultId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InfrastructureIndex = table.Column<int>(type: "int", nullable: false),
                    InfrastructureType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    FtthStatus = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ServiceProviderId = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    IsImmediatelyAvailable = table.Column<bool>(type: "bit", nullable: false),
                    FibreMaxSpeedMbps = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    ProductCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ProductName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    UpstreamSpeed = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    DownstreamSpeed = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    UpstreamMbps = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    DownstreamMbps = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenserveQualificationProducts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenserveQualificationProducts_OpenserveQualificationResults_QualificationResultId",
                        column: x => x.QualificationResultId,
                        principalTable: "OpenserveQualificationResults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_OpenserveQualificationResultId",
                table: "Orders",
                column: "OpenserveQualificationResultId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderIntents_OpenserveQualificationResultId",
                table: "OrderIntents",
                column: "OpenserveQualificationResultId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveQualificationProducts_ProductCode",
                table: "OpenserveQualificationProducts",
                column: "ProductCode");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveQualificationProducts_QualificationResultId",
                table: "OpenserveQualificationProducts",
                column: "QualificationResultId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveQualificationResults_Amid",
                table: "OpenserveQualificationResults",
                column: "Amid");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveQualificationResults_OrderId",
                table: "OpenserveQualificationResults",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveQualificationResults_QualifiedAtUtc",
                table: "OpenserveQualificationResults",
                column: "QualifiedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveQualificationResults_QueryLatitude_QueryLongitude_QualifiedAtUtc",
                table: "OpenserveQualificationResults",
                columns: new[] { "QueryLatitude", "QueryLongitude", "QualifiedAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_OpenserveQualificationResults_OpenserveQualificationResultId",
                table: "Orders",
                column: "OpenserveQualificationResultId",
                principalTable: "OpenserveQualificationResults",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_OpenserveQualificationResults_OpenserveQualificationResultId",
                table: "Orders");

            migrationBuilder.DropTable(
                name: "OpenserveQualificationProducts");

            migrationBuilder.DropTable(
                name: "OpenserveQualificationResults");

            migrationBuilder.DropIndex(
                name: "IX_Orders_OpenserveQualificationResultId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_OrderIntents_OpenserveQualificationResultId",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "OpenserveQualificationResultId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OpenserveQualificationResultId",
                table: "OrderIntents");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCoverageRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoverageRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ServicePackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequestedServiceType = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    AddressLine1 = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    AddressLine2 = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    Suburb = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    City = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Province = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    PostalCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Latitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    Longitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    GooglePlaceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MapProviderReference = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CustomerNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AdminNotes = table.Column<string>(type: "nvarchar(3000)", maxLength: 3000, nullable: true),
                    CoverageResultSummary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReviewedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoverageRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CoverageRequests_AspNetUsers_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CoverageRequests_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CoverageRequests_CustomerProfiles_CustomerProfileId",
                        column: x => x.CustomerProfileId,
                        principalTable: "CustomerProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CoverageRequests_ServicePackages_ServicePackageId",
                        column: x => x.ServicePackageId,
                        principalTable: "ServicePackages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoverageRequests_City",
                table: "CoverageRequests",
                column: "City");

            migrationBuilder.CreateIndex(
                name: "IX_CoverageRequests_CreatedAtUtc",
                table: "CoverageRequests",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CoverageRequests_CustomerProfileId",
                table: "CoverageRequests",
                column: "CustomerProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_CoverageRequests_PostalCode",
                table: "CoverageRequests",
                column: "PostalCode");

            migrationBuilder.CreateIndex(
                name: "IX_CoverageRequests_Province",
                table: "CoverageRequests",
                column: "Province");

            migrationBuilder.CreateIndex(
                name: "IX_CoverageRequests_RequestedServiceType",
                table: "CoverageRequests",
                column: "RequestedServiceType");

            migrationBuilder.CreateIndex(
                name: "IX_CoverageRequests_ReviewedAtUtc",
                table: "CoverageRequests",
                column: "ReviewedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CoverageRequests_ReviewedByUserId",
                table: "CoverageRequests",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CoverageRequests_ServicePackageId",
                table: "CoverageRequests",
                column: "ServicePackageId");

            migrationBuilder.CreateIndex(
                name: "IX_CoverageRequests_Source",
                table: "CoverageRequests",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_CoverageRequests_Status",
                table: "CoverageRequests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_CoverageRequests_Suburb",
                table: "CoverageRequests",
                column: "Suburb");

            migrationBuilder.CreateIndex(
                name: "IX_CoverageRequests_UserId",
                table: "CoverageRequests",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoverageRequests");
        }
    }
}

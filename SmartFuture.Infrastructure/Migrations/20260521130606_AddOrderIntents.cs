using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrderIntents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IntentToken = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ServicePackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    AddressLine1 = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
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
                    RequestedInstallationDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CustomerNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ClaimedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConvertedOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClaimedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConvertedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderIntents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderIntents_AspNetUsers_ClaimedByUserId",
                        column: x => x.ClaimedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OrderIntents_Orders_ConvertedOrderId",
                        column: x => x.ConvertedOrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OrderIntents_ServicePackages_ServicePackageId",
                        column: x => x.ServicePackageId,
                        principalTable: "ServicePackages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderIntents_ClaimedByUserId",
                table: "OrderIntents",
                column: "ClaimedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderIntents_ConvertedOrderId",
                table: "OrderIntents",
                column: "ConvertedOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderIntents_Email",
                table: "OrderIntents",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_OrderIntents_ExpiresAtUtc",
                table: "OrderIntents",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OrderIntents_IntentToken",
                table: "OrderIntents",
                column: "IntentToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderIntents_ServicePackageId",
                table: "OrderIntents",
                column: "ServicePackageId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderIntents_Status",
                table: "OrderIntents",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderIntents");
        }
    }
}

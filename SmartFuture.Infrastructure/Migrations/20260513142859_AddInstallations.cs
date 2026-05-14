using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInstallations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Installations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstallationNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    ScheduledForUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RescheduledFromUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TechnicianName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TechnicianPhone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TechnicianEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    TechnicianUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
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
                    TechnicianNotes = table.Column<string>(type: "nvarchar(3000)", maxLength: 3000, nullable: true),
                    CompletionNotes = table.Column<string>(type: "nvarchar(3000)", maxLength: 3000, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LastStatusChangedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Installations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Installations_AspNetUsers_LastStatusChangedByUserId",
                        column: x => x.LastStatusChangedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Installations_AspNetUsers_TechnicianUserId",
                        column: x => x.TechnicianUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Installations_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Installations_CancelledAtUtc",
                table: "Installations",
                column: "CancelledAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_City",
                table: "Installations",
                column: "City");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_CompletedAtUtc",
                table: "Installations",
                column: "CompletedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_CreatedAtUtc",
                table: "Installations",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_FailedAtUtc",
                table: "Installations",
                column: "FailedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_InstallationNumber",
                table: "Installations",
                column: "InstallationNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Installations_LastStatusChangedByUserId",
                table: "Installations",
                column: "LastStatusChangedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_OrderId",
                table: "Installations",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_PostalCode",
                table: "Installations",
                column: "PostalCode");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_Province",
                table: "Installations",
                column: "Province");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_ScheduledForUtc",
                table: "Installations",
                column: "ScheduledForUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_Source",
                table: "Installations",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_Status",
                table: "Installations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_Suburb",
                table: "Installations",
                column: "Suburb");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_TechnicianUserId",
                table: "Installations",
                column: "TechnicianUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Installations");
        }
    }
}

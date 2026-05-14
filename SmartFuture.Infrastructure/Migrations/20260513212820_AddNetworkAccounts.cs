using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNetworkAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NetworkAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Username = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    ProviderName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    ProviderReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PackageType = table.Column<int>(type: "int", nullable: false),
                    PackageName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PackageSpeedLabel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PackagePrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ProvisionedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SuspendedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResumedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TerminatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastPackageChangeAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AdminNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LastFailureReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SuspensionReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    TerminationReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LastStatusChangedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetworkAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NetworkAccounts_AspNetUsers_LastStatusChangedByUserId",
                        column: x => x.LastStatusChangedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NetworkAccounts_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NetworkAccounts_AccountNumber",
                table: "NetworkAccounts",
                column: "AccountNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NetworkAccounts_CreatedAtUtc",
                table: "NetworkAccounts",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkAccounts_LastStatusChangedByUserId",
                table: "NetworkAccounts",
                column: "LastStatusChangedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkAccounts_OrderId",
                table: "NetworkAccounts",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkAccounts_PackageType",
                table: "NetworkAccounts",
                column: "PackageType");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkAccounts_ProviderName",
                table: "NetworkAccounts",
                column: "ProviderName");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkAccounts_ProviderReference",
                table: "NetworkAccounts",
                column: "ProviderReference");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkAccounts_ProvisionedAtUtc",
                table: "NetworkAccounts",
                column: "ProvisionedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkAccounts_Status",
                table: "NetworkAccounts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkAccounts_TerminatedAtUtc",
                table: "NetworkAccounts",
                column: "TerminatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkAccounts_Username",
                table: "NetworkAccounts",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NetworkAccounts");
        }
    }
}

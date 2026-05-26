using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceChangeRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ServiceChangeRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NetworkAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CurrentPackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CurrentPackageName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    CurrentMonthlyPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrentBillingCycle = table.Column<int>(type: "int", nullable: false),
                    RequestedPackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedPackageName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    RequestedMonthlyPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    RequestedBillingCycle = table.Column<int>(type: "int", nullable: false),
                    ChangeType = table.Column<int>(type: "int", nullable: false),
                    EffectiveMode = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    ProRataAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ProRataCycleDays = table.Column<int>(type: "int", nullable: false),
                    ProRataRemainingDays = table.Column<int>(type: "int", nullable: false),
                    EffectiveDateUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomerNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AdminNotes = table.Column<string>(type: "nvarchar(3000)", maxLength: 3000, nullable: true),
                    AppliedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RejectedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RejectionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LastStatusChangedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceChangeRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceChangeRequests_AspNetUsers_LastStatusChangedByUserId",
                        column: x => x.LastStatusChangedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ServiceChangeRequests_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ServiceChangeRequests_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ServiceChangeRequests_NetworkAccounts_NetworkAccountId",
                        column: x => x.NetworkAccountId,
                        principalTable: "NetworkAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ServiceChangeRequests_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ServiceChangeRequests_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ServiceChangeRequests_ServicePackages_RequestedPackageId",
                        column: x => x.RequestedPackageId,
                        principalTable: "ServicePackages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceChangeRequests_ChangeType",
                table: "ServiceChangeRequests",
                column: "ChangeType");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceChangeRequests_CreatedAtUtc",
                table: "ServiceChangeRequests",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceChangeRequests_EffectiveDateUtc",
                table: "ServiceChangeRequests",
                column: "EffectiveDateUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceChangeRequests_EffectiveMode",
                table: "ServiceChangeRequests",
                column: "EffectiveMode");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceChangeRequests_InvoiceId",
                table: "ServiceChangeRequests",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceChangeRequests_LastStatusChangedByUserId",
                table: "ServiceChangeRequests",
                column: "LastStatusChangedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceChangeRequests_NetworkAccountId",
                table: "ServiceChangeRequests",
                column: "NetworkAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceChangeRequests_OrderId",
                table: "ServiceChangeRequests",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceChangeRequests_PaymentId",
                table: "ServiceChangeRequests",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceChangeRequests_RequestedPackageId",
                table: "ServiceChangeRequests",
                column: "RequestedPackageId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceChangeRequests_RequestNumber",
                table: "ServiceChangeRequests",
                column: "RequestNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceChangeRequests_Source",
                table: "ServiceChangeRequests",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceChangeRequests_Status",
                table: "ServiceChangeRequests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceChangeRequests_UserId",
                table: "ServiceChangeRequests",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServiceChangeRequests");
        }
    }
}

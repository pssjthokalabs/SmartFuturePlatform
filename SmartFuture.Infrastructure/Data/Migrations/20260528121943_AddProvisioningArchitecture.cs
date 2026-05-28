using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProvisioningArchitecture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BurstSpeedMbps",
                table: "ServicePackages",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProvisioningType",
                table: "ServicePackages",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RadiusProfileId",
                table: "ServicePackages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresProvisioning",
                table: "ServicePackages",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CurrentIpAddress",
                table: "NetworkAccounts",
                type: "nvarchar(45)",
                maxLength: 45,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastProvisioningAttemptUtc",
                table: "NetworkAccounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NasIdentifier",
                table: "NetworkAccounts",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PasswordHash",
                table: "NetworkAccounts",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProvisioningAttemptCount",
                table: "NetworkAccounts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ProvisioningStatus",
                table: "NetworkAccounts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "RadiusProfileId",
                table: "NetworkAccounts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProvisioningEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NetworkAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<int>(type: "int", nullable: false),
                    ProviderName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    ProviderReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsSuccess = table.Column<bool>(type: "bit", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    MetadataJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    TriggeredByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisioningEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProvisioningEvents_AspNetUsers_TriggeredByUserId",
                        column: x => x.TriggeredByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProvisioningEvents_NetworkAccounts_NetworkAccountId",
                        column: x => x.NetworkAccountId,
                        principalTable: "NetworkAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RadiusProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DownloadMbps = table.Column<int>(type: "int", nullable: false),
                    UploadMbps = table.Column<int>(type: "int", nullable: false),
                    BurstMbps = table.Column<int>(type: "int", nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RadiusProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServicePackages_RadiusProfileId",
                table: "ServicePackages",
                column: "RadiusProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePackages_RequiresProvisioning",
                table: "ServicePackages",
                column: "RequiresProvisioning");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkAccounts_ProvisioningStatus",
                table: "NetworkAccounts",
                column: "ProvisioningStatus");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkAccounts_RadiusProfileId",
                table: "NetworkAccounts",
                column: "RadiusProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningEvents_CreatedAtUtc",
                table: "ProvisioningEvents",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningEvents_EventType",
                table: "ProvisioningEvents",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningEvents_IsSuccess",
                table: "ProvisioningEvents",
                column: "IsSuccess");

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningEvents_NetworkAccountId",
                table: "ProvisioningEvents",
                column: "NetworkAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningEvents_TriggeredByUserId",
                table: "ProvisioningEvents",
                column: "TriggeredByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RadiusProfiles_IsActive",
                table: "RadiusProfiles",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_RadiusProfiles_Name",
                table: "RadiusProfiles",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_NetworkAccounts_RadiusProfiles_RadiusProfileId",
                table: "NetworkAccounts",
                column: "RadiusProfileId",
                principalTable: "RadiusProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ServicePackages_RadiusProfiles_RadiusProfileId",
                table: "ServicePackages",
                column: "RadiusProfileId",
                principalTable: "RadiusProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NetworkAccounts_RadiusProfiles_RadiusProfileId",
                table: "NetworkAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_ServicePackages_RadiusProfiles_RadiusProfileId",
                table: "ServicePackages");

            migrationBuilder.DropTable(
                name: "ProvisioningEvents");

            migrationBuilder.DropTable(
                name: "RadiusProfiles");

            migrationBuilder.DropIndex(
                name: "IX_ServicePackages_RadiusProfileId",
                table: "ServicePackages");

            migrationBuilder.DropIndex(
                name: "IX_ServicePackages_RequiresProvisioning",
                table: "ServicePackages");

            migrationBuilder.DropIndex(
                name: "IX_NetworkAccounts_ProvisioningStatus",
                table: "NetworkAccounts");

            migrationBuilder.DropIndex(
                name: "IX_NetworkAccounts_RadiusProfileId",
                table: "NetworkAccounts");

            migrationBuilder.DropColumn(
                name: "BurstSpeedMbps",
                table: "ServicePackages");

            migrationBuilder.DropColumn(
                name: "ProvisioningType",
                table: "ServicePackages");

            migrationBuilder.DropColumn(
                name: "RadiusProfileId",
                table: "ServicePackages");

            migrationBuilder.DropColumn(
                name: "RequiresProvisioning",
                table: "ServicePackages");

            migrationBuilder.DropColumn(
                name: "CurrentIpAddress",
                table: "NetworkAccounts");

            migrationBuilder.DropColumn(
                name: "LastProvisioningAttemptUtc",
                table: "NetworkAccounts");

            migrationBuilder.DropColumn(
                name: "NasIdentifier",
                table: "NetworkAccounts");

            migrationBuilder.DropColumn(
                name: "PasswordHash",
                table: "NetworkAccounts");

            migrationBuilder.DropColumn(
                name: "ProvisioningAttemptCount",
                table: "NetworkAccounts");

            migrationBuilder.DropColumn(
                name: "ProvisioningStatus",
                table: "NetworkAccounts");

            migrationBuilder.DropColumn(
                name: "RadiusProfileId",
                table: "NetworkAccounts");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenserveIntegrationConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OpenserveIntegrationConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: true),
                    BaseUrl = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    WsIspCode = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    IspIdentifier = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    SenderId = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ReplyToAddress = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    EventNotificationUrl = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    HttpTimeoutSeconds = table.Column<int>(type: "int", nullable: true),
                    PollingFallbackIntervalMinutes = table.Column<int>(type: "int", nullable: true),
                    CallbackAuthMode = table.Column<int>(type: "int", nullable: true),
                    AllowedIpRangesCsv = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ApiKeyProtected = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SharedSecretProtected = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenserveIntegrationConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenserveIntegrationConfigs_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveIntegrationConfigs_UpdatedByUserId",
                table: "OpenserveIntegrationConfigs",
                column: "UpdatedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OpenserveIntegrationConfigs");
        }
    }
}

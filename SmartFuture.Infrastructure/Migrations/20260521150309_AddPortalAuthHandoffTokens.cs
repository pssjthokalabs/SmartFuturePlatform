using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPortalAuthHandoffTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PortalAuthHandoffTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IntentToken = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Purpose = table.Column<int>(type: "int", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedIpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedUserAgent = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ConsumedIpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ConsumedUserAgent = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortalAuthHandoffTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PortalAuthHandoffTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PortalAuthHandoffTokens_ConsumedAtUtc",
                table: "PortalAuthHandoffTokens",
                column: "ConsumedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PortalAuthHandoffTokens_ExpiresAtUtc",
                table: "PortalAuthHandoffTokens",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PortalAuthHandoffTokens_TokenHash",
                table: "PortalAuthHandoffTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PortalAuthHandoffTokens_UserId",
                table: "PortalAuthHandoffTokens",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PortalAuthHandoffTokens");
        }
    }
}

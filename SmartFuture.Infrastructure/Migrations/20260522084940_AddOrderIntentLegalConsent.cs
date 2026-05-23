using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderIntentLegalConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PrivacyAcknowledgedAtUtc",
                table: "OrderIntents",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrivacyVersion",
                table: "OrderIntents",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TermsAcceptedAtUtc",
                table: "OrderIntents",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TermsVersion",
                table: "OrderIntents",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PrivacyAcknowledgedAtUtc",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "PrivacyVersion",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "TermsAcceptedAtUtc",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "TermsVersion",
                table: "OrderIntents");
        }
    }
}

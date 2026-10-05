using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenserveAddressVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AddressCandidateCount",
                table: "OpenserveQualificationResults",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AddressCandidatesJson",
                table: "OpenserveQualificationResults",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AddressResolution",
                table: "OpenserveQualificationResults",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AddressResolutionDetail",
                table: "OpenserveQualificationResults",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressResolutionNote",
                table: "OpenserveQualificationResults",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AddressResolvedAtUtc",
                table: "OpenserveQualificationResults",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AddressResolvedByUserId",
                table: "OpenserveQualificationResults",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AddressVerifiedAtUtc",
                table: "OpenserveQualificationResults",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AddressVerifyIntegrationLogId",
                table: "OpenserveQualificationResults",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AddressCandidateCount",
                table: "OpenserveQualificationResults");

            migrationBuilder.DropColumn(
                name: "AddressCandidatesJson",
                table: "OpenserveQualificationResults");

            migrationBuilder.DropColumn(
                name: "AddressResolution",
                table: "OpenserveQualificationResults");

            migrationBuilder.DropColumn(
                name: "AddressResolutionDetail",
                table: "OpenserveQualificationResults");

            migrationBuilder.DropColumn(
                name: "AddressResolutionNote",
                table: "OpenserveQualificationResults");

            migrationBuilder.DropColumn(
                name: "AddressResolvedAtUtc",
                table: "OpenserveQualificationResults");

            migrationBuilder.DropColumn(
                name: "AddressResolvedByUserId",
                table: "OpenserveQualificationResults");

            migrationBuilder.DropColumn(
                name: "AddressVerifiedAtUtc",
                table: "OpenserveQualificationResults");

            migrationBuilder.DropColumn(
                name: "AddressVerifyIntegrationLogId",
                table: "OpenserveQualificationResults");
        }
    }
}

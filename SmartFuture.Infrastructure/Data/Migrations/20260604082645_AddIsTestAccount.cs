using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIsTestAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTestAccount",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_IsTestAccount",
                table: "AspNetUsers",
                column: "IsTestAccount");

            // Backfill existing controlled test accounts: customer{1000-1999}@gmail.com.
            // Matched on the uppercase NormalizedEmail so the result is the same
            // regardless of the column's collation case-sensitivity. The "[0-9]"
            // ranges each match exactly one digit, so "CUSTOMER1[0-9][0-9][0-9]"
            // is precisely customer1000..customer1999. The dot is a literal in LIKE.
            // Idempotent — re-running only re-sets the same rows.
            migrationBuilder.Sql(
                "UPDATE [AspNetUsers] SET [IsTestAccount] = 1 " +
                "WHERE [NormalizedEmail] LIKE 'CUSTOMER1[0-9][0-9][0-9]@GMAIL.COM';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_IsTestAccount",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "IsTestAccount",
                table: "AspNetUsers");
        }
    }
}

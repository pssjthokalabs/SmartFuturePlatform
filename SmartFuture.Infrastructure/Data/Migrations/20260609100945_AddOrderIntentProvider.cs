using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderIntentProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PaymentProviderType.Paystack == 3. Existing OrderIntent
            // rows are all Paystack-initiated (the only intent path
            // pre-PayFast), so 3 is the correct backfill value. The
            // default also covers any future code path that creates
            // an intent without setting Provider explicitly.
            migrationBuilder.AddColumn<int>(
                name: "Provider",
                table: "OrderIntents",
                type: "int",
                nullable: false,
                defaultValue: 3);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Provider",
                table: "OrderIntents");
        }
    }
}

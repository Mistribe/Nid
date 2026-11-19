using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CardMinimumUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "card_count_withdrawals",
                schema: "banking",
                table: "account",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "card_min_payments",
                schema: "banking",
                table: "account",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_account_card_min_payments",
                schema: "banking",
                table: "account",
                sql: "card_min_payments is null or (kind = 'card' and card_min_payments between 1 and 31)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_account_card_min_payments",
                schema: "banking",
                table: "account");

            migrationBuilder.DropColumn(
                name: "card_count_withdrawals",
                schema: "banking",
                table: "account");

            migrationBuilder.DropColumn(
                name: "card_min_payments",
                schema: "banking",
                table: "account");
        }
    }
}

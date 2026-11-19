using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankAccountNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "card_debit",
                schema: "banking",
                table: "account",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "household_name",
                schema: "banking",
                table: "account",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_account_card_debit",
                schema: "banking",
                table: "account",
                sql: "card_debit is null or card_debit in ('immediate','deferred')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_account_household_name",
                schema: "banking",
                table: "account",
                sql: "household_name is null or char_length(household_name) between 1 and 100");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_account_card_debit",
                schema: "banking",
                table: "account");

            migrationBuilder.DropCheckConstraint(
                name: "ck_account_household_name",
                schema: "banking",
                table: "account");

            migrationBuilder.DropColumn(
                name: "card_debit",
                schema: "banking",
                table: "account");

            migrationBuilder.DropColumn(
                name: "household_name",
                schema: "banking",
                table: "account");
        }
    }
}

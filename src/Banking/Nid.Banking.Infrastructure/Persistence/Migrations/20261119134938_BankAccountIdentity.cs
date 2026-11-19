using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankAccountIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "holder",
                schema: "banking",
                table: "account",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "product",
                schema: "banking",
                table: "account",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_account_holder",
                schema: "banking",
                table: "account",
                sql: "holder is null or char_length(holder) between 1 and 200");

            migrationBuilder.AddCheckConstraint(
                name: "ck_account_product",
                schema: "banking",
                table: "account",
                sql: "product is null or char_length(product) between 1 and 200");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_account_holder",
                schema: "banking",
                table: "account");

            migrationBuilder.DropCheckConstraint(
                name: "ck_account_product",
                schema: "banking",
                table: "account");

            migrationBuilder.DropColumn(
                name: "holder",
                schema: "banking",
                table: "account");

            migrationBuilder.DropColumn(
                name: "product",
                schema: "banking",
                table: "account");
        }
    }
}

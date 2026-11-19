using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankCategoryRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "category_rule",
                schema: "banking",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pattern = table.Column<string>(type: "text", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    direction = table.Column<string>(type: "text", nullable: true),
                    min_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    max_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_category_rule", x => x.id);
                    table.CheckConstraint("ck_category_rule_amounts", "(min_amount is null or min_amount >= 0) and (max_amount is null or max_amount >= 0) and (min_amount is null or max_amount is null or min_amount <= max_amount)");
                    table.CheckConstraint("ck_category_rule_direction", "direction is null or direction in ('debit','credit')");
                    table.CheckConstraint("ck_category_rule_pattern", "char_length(pattern) between 2 and 60");
                    table.ForeignKey(
                        name: "fk_category_rule_account_account_id",
                        column: x => x.account_id,
                        principalSchema: "banking",
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_category_rule_category_category_id",
                        column: x => x.category_id,
                        principalSchema: "banking",
                        principalTable: "category",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_transaction_category_rule_id",
                schema: "banking",
                table: "transaction",
                column: "category_rule_id");

            migrationBuilder.CreateIndex(
                name: "ix_category_rule_account_id",
                schema: "banking",
                table: "category_rule",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_category_rule_category_id",
                schema: "banking",
                table: "category_rule",
                column: "category_id");

            migrationBuilder.AddForeignKey(
                name: "fk_transaction_category_rule_category_rule_id",
                schema: "banking",
                table: "transaction",
                column: "category_rule_id",
                principalSchema: "banking",
                principalTable: "category_rule",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_transaction_category_rule_category_rule_id",
                schema: "banking",
                table: "transaction");

            migrationBuilder.DropTable(
                name: "category_rule",
                schema: "banking");

            migrationBuilder.DropIndex(
                name: "ix_transaction_category_rule_id",
                schema: "banking",
                table: "transaction");
        }
    }
}

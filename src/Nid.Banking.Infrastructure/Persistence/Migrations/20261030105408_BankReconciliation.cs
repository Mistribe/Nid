using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "reconciled_at",
                schema: "banking",
                table: "transaction",
                type: "timestamp with time zone",
                nullable: true);

            // 5B's history is not for the job: only the lines read from now on are.
            migrationBuilder.Sql("UPDATE banking.transaction SET reconciled_at = created_at;");

            migrationBuilder.CreateTable(
                name: "link",
                schema: "banking",
                columns: table => new
                {
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_module = table.Column<string>(type: "text", nullable: true),
                    target_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_key = table.Column<string>(type: "text", nullable: true),
                    counterpart_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    expected_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    expected_currency = table.Column<string>(type: "char(3)", nullable: true),
                    decided_by = table.Column<string>(type: "text", nullable: false),
                    decided_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_link", x => x.transaction_id);
                    table.CheckConstraint("ck_link_outcome", "outcome in ('as_expected','price_differs','income','contribution','internal_transfer','card_settlement')");
                    table.ForeignKey(
                        name: "fk_link_transaction_transaction_id",
                        column: x => x.transaction_id,
                        principalSchema: "banking",
                        principalTable: "transaction",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recurring_group",
                schema: "banking",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label_stem = table.Column<string>(type: "text", nullable: false),
                    proposed_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recurring_group", x => x.id);
                    table.ForeignKey(
                        name: "fk_recurring_group_account_account_id",
                        column: x => x.account_id,
                        principalSchema: "banking",
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rule",
                schema: "banking",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label_stem = table.Column<string>(type: "text", nullable: false),
                    target_module = table.Column<string>(type: "text", nullable: false),
                    target_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rule", x => x.id);
                    table.ForeignKey(
                        name: "fk_rule_account_account_id",
                        column: x => x.account_id,
                        principalSchema: "banking",
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_link_target_item_id",
                schema: "banking",
                table: "link",
                column: "target_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_recurring_group_account_id_label_stem",
                schema: "banking",
                table: "recurring_group",
                columns: new[] { "account_id", "label_stem" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rule_account_id_created_by_person_id_label_stem",
                schema: "banking",
                table: "rule",
                columns: new[] { "account_id", "created_by_person_id", "label_stem" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "link",
                schema: "banking");

            migrationBuilder.DropTable(
                name: "recurring_group",
                schema: "banking");

            migrationBuilder.DropTable(
                name: "rule",
                schema: "banking");

            migrationBuilder.DropColumn(
                name: "reconciled_at",
                schema: "banking",
                table: "transaction");
        }
    }
}

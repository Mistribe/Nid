using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "transaction",
                schema: "banking",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_ref = table.Column<string>(type: "text", nullable: false),
                    fingerprint = table.Column<string>(type: "text", nullable: false),
                    booked_on = table.Column<LocalDate>(type: "date", nullable: false),
                    value_on = table.Column<LocalDate>(type: "date", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    counterparty = table.Column<string>(type: "text", nullable: true),
                    remittance = table.Column<string>(type: "text", nullable: true),
                    normalized_label = table.Column<string>(type: "text", nullable: false),
                    link_state = table.Column<string>(type: "text", nullable: false),
                    fetched_via = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transaction", x => x.id);
                    table.CheckConstraint("ck_transaction_fetched_via", "fetched_via in ('api','file')");
                    table.CheckConstraint("ck_transaction_link_state", "link_state in ('unlinked','linked','ignored','internal')");
                    table.ForeignKey(
                        name: "fk_transaction_account_account_id",
                        column: x => x.account_id,
                        principalSchema: "banking",
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_transaction_account_id_booked_on",
                schema: "banking",
                table: "transaction",
                columns: new[] { "account_id", "booked_on" });

            migrationBuilder.CreateIndex(
                name: "ix_transaction_account_id_fingerprint",
                schema: "banking",
                table: "transaction",
                columns: new[] { "account_id", "fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transaction_account_id_provider_ref",
                schema: "banking",
                table: "transaction",
                columns: new[] { "account_id", "provider_ref" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "transaction",
                schema: "banking");
        }
    }
}

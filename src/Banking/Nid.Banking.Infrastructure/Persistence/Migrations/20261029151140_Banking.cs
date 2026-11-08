using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Banking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "banking");

            migrationBuilder.CreateTable(
                name: "account",
                schema: "banking",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    iban_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    iban_masked = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    envelope_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    card_of_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    auto_apply_balance = table.Column<bool>(type: "boolean", nullable: false),
                    ignored = table.Column<bool>(type: "boolean", nullable: false),
                    balance_conflict_on = table.Column<LocalDate>(type: "date", nullable: true),
                    associated_on = table.Column<LocalDate>(type: "date", nullable: true),
                    associated_by_person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_valued_on = table.Column<LocalDate>(type: "date", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account", x => x.id);
                    table.CheckConstraint("ck_account_iban_masked", "iban_masked is null or char_length(iban_masked) <= 4");
                    table.CheckConstraint("ck_account_kind", "kind in ('checking','savings','card','other')");
                    table.ForeignKey(
                        name: "fk_account_account_card_of_account_id",
                        column: x => x.card_of_account_id,
                        principalSchema: "banking",
                        principalTable: "account",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "connection",
                schema: "banking",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    aspsp_name = table.Column<string>(type: "text", nullable: false),
                    aspsp_country = table.Column<string>(type: "text", nullable: false),
                    session_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    state_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    state_expires_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    valid_until = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    last_synced_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "text", nullable: true),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connection", x => x.id);
                    table.CheckConstraint("ck_connection_source", "source in ('enable_banking')");
                    table.CheckConstraint("ck_connection_status", "status in ('pending','active','expired','revoked','error')");
                });

            migrationBuilder.CreateTable(
                name: "sync_run",
                schema: "banking",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    trigger = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    connections = table.Column<int>(type: "integer", nullable: false),
                    accounts = table.Column<int>(type: "integer", nullable: false),
                    balances = table.Column<int>(type: "integer", nullable: false),
                    proposals = table.Column<int>(type: "integer", nullable: false),
                    written = table.Column<int>(type: "integer", nullable: false),
                    skipped = table.Column<int>(type: "integer", nullable: false),
                    failure_code = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_run", x => x.id);
                    table.CheckConstraint("ck_sync_run_outcome", "outcome in ('running','succeeded','failed')");
                    table.CheckConstraint("ck_sync_run_trigger", "trigger in ('schedule','manual')");
                });

            migrationBuilder.CreateTable(
                name: "balance",
                schema: "banking",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    as_of = table.Column<LocalDate>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    balance_type = table.Column<string>(type: "text", nullable: false),
                    fetched_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_balance", x => new { x.account_id, x.as_of });
                    table.ForeignKey(
                        name: "fk_balance_account_account_id",
                        column: x => x.account_id,
                        principalSchema: "banking",
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "account_source",
                schema: "banking",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_uid = table.Column<string>(type: "text", nullable: false),
                    identification_hash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_source", x => new { x.account_id, x.connection_id });
                    table.ForeignKey(
                        name: "fk_account_source_account_account_id",
                        column: x => x.account_id,
                        principalSchema: "banking",
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_account_source_connection_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "banking",
                        principalTable: "connection",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_account_card_of_account_id",
                schema: "banking",
                table: "account",
                column: "card_of_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_account_envelope_item_id",
                schema: "banking",
                table: "account",
                column: "envelope_item_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_account_iban_hash",
                schema: "banking",
                table: "account",
                column: "iban_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_account_source_connection_id",
                schema: "banking",
                table: "account_source",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_account_source_identification_hash",
                schema: "banking",
                table: "account_source",
                column: "identification_hash");

            migrationBuilder.CreateIndex(
                name: "ix_connection_person_id",
                schema: "banking",
                table: "connection",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "ix_connection_state_hash",
                schema: "banking",
                table: "connection",
                column: "state_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sync_run_outcome",
                schema: "banking",
                table: "sync_run",
                column: "outcome",
                unique: true,
                filter: "outcome = 'running'");

            migrationBuilder.CreateIndex(
                name: "ix_sync_run_started_at",
                schema: "banking",
                table: "sync_run",
                column: "started_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_source",
                schema: "banking");

            migrationBuilder.DropTable(
                name: "balance",
                schema: "banking");

            migrationBuilder.DropTable(
                name: "sync_run",
                schema: "banking");

            migrationBuilder.DropTable(
                name: "connection",
                schema: "banking");

            migrationBuilder.DropTable(
                name: "account",
                schema: "banking");
        }
    }
}

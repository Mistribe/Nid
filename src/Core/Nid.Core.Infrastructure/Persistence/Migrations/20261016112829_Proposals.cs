using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Proposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "proposal",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    target_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    for_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_key = table.Column<string>(type: "text", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    payload_version = table.Column<int>(type: "integer", nullable: false),
                    edited_payload = table.Column<string>(type: "jsonb", nullable: true),
                    warnings = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    fingerprint = table.Column<string>(type: "char(64)", nullable: false),
                    superseded_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_via = table.Column<string>(type: "text", nullable: false),
                    token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_label = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    decided_by_person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "text", nullable: true),
                    applied_refs = table.Column<Guid[]>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_proposal", x => x.id);
                    table.CheckConstraint("ck_proposal_created_via", "created_via in ('ui','api','mcp')");
                    table.CheckConstraint("ck_proposal_status", "status in ('pending','approved','rejected','superseded')");
                    table.ForeignKey(
                        name: "fk_proposal_item_target_item_id",
                        column: x => x.target_item_id,
                        principalSchema: "core",
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_proposal_person_for_person_id",
                        column: x => x.for_person_id,
                        principalSchema: "core",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_proposal_fingerprint",
                schema: "core",
                table: "proposal",
                column: "fingerprint",
                unique: true,
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_proposal_for_person_id",
                schema: "core",
                table: "proposal",
                column: "for_person_id");

            migrationBuilder.CreateIndex(
                name: "ix_proposal_kind_target_item_id_subject_key",
                schema: "core",
                table: "proposal",
                columns: new[] { "kind", "target_item_id", "subject_key" },
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_proposal_target_item_id_status",
                schema: "core",
                table: "proposal",
                columns: new[] { "target_item_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "proposal",
                schema: "core");
        }
    }
}

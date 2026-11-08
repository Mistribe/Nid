using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DatedFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "flow",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_on = table.Column<LocalDate>(type: "date", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    category = table.Column<string>(type: "text", nullable: true),
                    external_ref = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    created_via = table.Column<string>(type: "text", nullable: false),
                    created_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    supersedes_id = table.Column<Guid>(type: "uuid", nullable: true),
                    retired_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_flow", x => x.id);
                    table.CheckConstraint("ck_flow_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_flow_created_via", "created_via in ('ui','api','mcp','job','import','admin-cli')");
                    table.CheckConstraint("ck_flow_kind", "kind in ('contribution','withdrawal','opening','income','expense')");
                    table.CheckConstraint("ck_flow_source", "source in ('manual','import')");
                    table.ForeignKey(
                        name: "fk_flow_flow_supersedes_id",
                        column: x => x.supersedes_id,
                        principalSchema: "core",
                        principalTable: "flow",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_flow_item_item_id",
                        column: x => x.item_id,
                        principalSchema: "core",
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_flow_person_created_by_person_id",
                        column: x => x.created_by_person_id,
                        principalSchema: "core",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "valuation",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    as_of = table.Column<LocalDate>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    created_via = table.Column<string>(type: "text", nullable: false),
                    created_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    supersedes_id = table.Column<Guid>(type: "uuid", nullable: true),
                    retired_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_valuation", x => x.id);
                    table.CheckConstraint("ck_valuation_created_via", "created_via in ('ui','api','mcp','job','import','admin-cli')");
                    table.CheckConstraint("ck_valuation_source", "source in ('manual','import')");
                    table.ForeignKey(
                        name: "fk_valuation_item_item_id",
                        column: x => x.item_id,
                        principalSchema: "core",
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_valuation_person_created_by_person_id",
                        column: x => x.created_by_person_id,
                        principalSchema: "core",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_valuation_valuation_supersedes_id",
                        column: x => x.supersedes_id,
                        principalSchema: "core",
                        principalTable: "valuation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_flow_created_by_person_id",
                schema: "core",
                table: "flow",
                column: "created_by_person_id");

            migrationBuilder.CreateIndex(
                name: "ix_flow_item_id_occurred_on",
                schema: "core",
                table: "flow",
                columns: new[] { "item_id", "occurred_on" },
                filter: "retired_at is null");

            migrationBuilder.CreateIndex(
                name: "ix_flow_supersedes_id",
                schema: "core",
                table: "flow",
                column: "supersedes_id");

            migrationBuilder.CreateIndex(
                name: "ux_flow_external_ref_current",
                schema: "core",
                table: "flow",
                columns: new[] { "item_id", "external_ref" },
                unique: true,
                filter: "external_ref is not null and retired_at is null");

            migrationBuilder.CreateIndex(
                name: "ux_flow_opening_current",
                schema: "core",
                table: "flow",
                column: "item_id",
                unique: true,
                filter: "kind = 'opening' and retired_at is null");

            migrationBuilder.CreateIndex(
                name: "ix_valuation_created_by_person_id",
                schema: "core",
                table: "valuation",
                column: "created_by_person_id");

            migrationBuilder.CreateIndex(
                name: "ix_valuation_supersedes_id",
                schema: "core",
                table: "valuation",
                column: "supersedes_id");

            migrationBuilder.CreateIndex(
                name: "ux_valuation_current",
                schema: "core",
                table: "valuation",
                columns: new[] { "item_id", "as_of" },
                unique: true,
                filter: "retired_at is null");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "flow",
                schema: "core");

            migrationBuilder.DropTable(
                name: "valuation",
                schema: "core");
        }
    }
}

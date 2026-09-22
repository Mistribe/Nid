using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "core");

            migrationBuilder.CreateTable(
                name: "household_settings",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    reference_currency = table.Column<string>(type: "char(3)", nullable: false),
                    timezone = table.Column<string>(type: "text", nullable: false),
                    locale = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_household_settings", x => x.id);
                    table.CheckConstraint("ck_household_settings_single_row", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "item",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    visibility = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: true),
                    archived_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item", x => x.id);
                    table.CheckConstraint("ck_item_visibility", "visibility in ('shared','private')");
                });

            migrationBuilder.CreateTable(
                name: "person",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    oidc_subject = table.Column<string>(type: "text", nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: true),
                    session_epoch = table.Column<int>(type: "integer", nullable: false),
                    preferences = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_person", x => x.id);
                    table.CheckConstraint("ck_person_kind", "kind in ('adult','dependent')");
                    table.CheckConstraint("ck_person_subject", "(kind = 'adult' and oidc_subject is not null) or (kind = 'dependent' and oidc_subject is null)");
                });

            migrationBuilder.CreateTable(
                name: "ownership",
                schema: "core",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    share_bp = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ownership", x => new { x.item_id, x.person_id });
                    table.CheckConstraint("ck_ownership_share", "share_bp between 1 and 10000");
                    table.ForeignKey(
                        name: "fk_ownership_item_item_id",
                        column: x => x.item_id,
                        principalSchema: "core",
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ownership_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "core",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_item_module_kind",
                schema: "core",
                table: "item",
                columns: new[] { "module", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_ownership_person_id",
                schema: "core",
                table: "ownership",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "ix_person_oidc_subject",
                schema: "core",
                table: "person",
                column: "oidc_subject",
                unique: true,
                filter: "oidc_subject is not null");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "household_settings",
                schema: "core");

            migrationBuilder.DropTable(
                name: "ownership",
                schema: "core");

            migrationBuilder.DropTable(
                name: "item",
                schema: "core");

            migrationBuilder.DropTable(
                name: "person",
                schema: "core");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "platform");

            migrationBuilder.CreateTable(
                name: "import_session",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    adults = table.Column<string>(type: "jsonb", nullable: false),
                    report = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_session", x => x.id);
                    table.CheckConstraint("ck_import_session_state", "state in ('open','validated','invalid','committed','cancelled','expired')");
                });

            migrationBuilder.CreateTable(
                name: "import_batch",
                schema: "platform",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section = table.Column<string>(type: "text", nullable: false),
                    batch_index = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<string>(type: "char(64)", nullable: false),
                    entity_count = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    received_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_batch", x => new { x.session_id, x.section, x.batch_index });
                    table.CheckConstraint("ck_import_batch_count", "entity_count >= 0");
                    table.CheckConstraint("ck_import_batch_index", "batch_index >= 0");
                    table.ForeignKey(
                        name: "fk_import_batch_import_session_session_id",
                        column: x => x.session_id,
                        principalSchema: "platform",
                        principalTable: "import_session",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_import_session_person_id",
                schema: "platform",
                table: "import_session",
                column: "person_id",
                unique: true,
                filter: "state in ('open','validated','invalid')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "import_batch",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "import_session",
                schema: "platform");
        }
    }
}

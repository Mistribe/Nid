using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    level = table.Column<string>(type: "text", nullable: false),
                    item_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'"),
                    due_on = table.Column<LocalDate>(type: "date", nullable: true),
                    notify_on = table.Column<LocalDate>(type: "date", nullable: false),
                    @params = table.Column<string>(name: "params", type: "jsonb", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    read_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    snoozed_until = table.Column<LocalDate>(type: "date", nullable: true),
                    digested_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    opened_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    resolved_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    dismissed_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification", x => x.id);
                    table.CheckConstraint("ck_notification_level", "level in ('critical','action','info')");
                    table.CheckConstraint("ck_notification_state", "state in ('pending','open','snoozed','dismissed','resolved')");
                });

            migrationBuilder.CreateTable(
                name: "notification_run",
                schema: "platform",
                columns: table => new
                {
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    ran_on = table.Column<LocalDate>(type: "date", nullable: false),
                    ran_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ok = table.Column<bool>(type: "boolean", nullable: false),
                    last_ok_on = table.Column<LocalDate>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_run", x => new { x.person_id, x.source });
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_person_id_key",
                schema: "platform",
                table: "notification",
                columns: new[] { "person_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_person_id_state",
                schema: "platform",
                table: "notification",
                columns: new[] { "person_id", "state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "notification_run",
                schema: "platform");
        }
    }
}

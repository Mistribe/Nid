using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nid.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                schema: "platform",
                columns: table => new
                {
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    actor_kind = table.Column<string>(type: "text", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    via = table.Column<string>(type: "text", nullable: false),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    request_id = table.Column<string>(type: "text", nullable: true),
                    request = table.Column<string>(type: "text", nullable: true),
                    entity = table.Column<string>(type: "text", nullable: true),
                    entity_key = table.Column<string>(type: "text", nullable: true),
                    item_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    subject_person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.seq);
                    table.CheckConstraint("ck_audit_log_category", "category in ('write','security','import')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_category_action_seq",
                schema: "platform",
                table: "audit_log",
                columns: new[] { "category", "action", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_item_ids",
                schema: "platform",
                table: "audit_log",
                column: "item_ids")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_subject_person_id_seq",
                schema: "platform",
                table: "audit_log",
                columns: new[] { "subject_person_id", "seq" },
                filter: "category = 'security'");

            // Spec U5: the application runs its own migrations, so it owns the table and grants cannot bind it;
            // a trigger does, whoever the role. TRUNCATE fires statement triggers only.
            migrationBuilder.Sql("""
                create function platform.audit_log_append_only() returns trigger language plpgsql as $$
                begin
                  raise exception 'platform.audit_log is append-only';
                end $$;
                create trigger audit_log_no_rewrite before update or delete on platform.audit_log
                  for each row execute function platform.audit_log_append_only();
                create trigger audit_log_no_truncate before truncate on platform.audit_log
                  for each statement execute function platform.audit_log_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                drop trigger audit_log_no_truncate on platform.audit_log;
                drop trigger audit_log_no_rewrite on platform.audit_log;
                drop function platform.audit_log_append_only();
                """);

            migrationBuilder.DropTable(
                name: "audit_log",
                schema: "platform");
        }
    }
}

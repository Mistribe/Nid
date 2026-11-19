using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LocalAccounts215 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_person_subject",
                schema: "core",
                table: "person");

            migrationBuilder.CreateTable(
                name: "credential_link",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    token_hash = table.Column<string>(type: "char(64)", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    via = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credential_link", x => x.id);
                    table.CheckConstraint("ck_credential_link_expiry", "expires_at > created_at");
                    table.CheckConstraint("ck_credential_link_purpose", "purpose in ('enroll','reset')");
                    table.CheckConstraint("ck_credential_link_via", "via in ('ui','admin-cli')");
                    table.ForeignKey(
                        name: "fk_credential_link_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "core",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "local_credential",
                schema: "core",
                columns: table => new
                {
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    login = table.Column<string>(type: "text", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    password_changed_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    failed_count = table.Column<int>(type: "integer", nullable: false),
                    last_failed_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    lock_level = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    last_login_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_local_credential", x => x.person_id);
                    table.CheckConstraint("ck_local_credential_counts", "failed_count >= 0 and lock_level >= 0");
                    table.CheckConstraint("ck_local_credential_login", "length(login) between 1 and 254 and login = lower(login)");
                    table.ForeignKey(
                        name: "fk_local_credential_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "core",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_person_subject",
                schema: "core",
                table: "person",
                sql: "kind = 'adult' or oidc_subject is null");

            migrationBuilder.CreateIndex(
                name: "ix_credential_link_live",
                schema: "core",
                table: "credential_link",
                column: "person_id",
                unique: true,
                filter: "used_at is null and revoked_at is null");

            migrationBuilder.CreateIndex(
                name: "ix_credential_link_token_hash",
                schema: "core",
                table: "credential_link",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_local_credential_login",
                schema: "core",
                table: "local_credential",
                column: "login",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "credential_link",
                schema: "core");

            migrationBuilder.DropTable(
                name: "local_credential",
                schema: "core");

            migrationBuilder.DropCheckConstraint(
                name: "ck_person_subject",
                schema: "core",
                table: "person");

            migrationBuilder.AddCheckConstraint(
                name: "ck_person_subject",
                schema: "core",
                table: "person",
                sql: "(kind = 'adult' and (oidc_subject is not null or email is not null)) or (kind = 'dependent' and oidc_subject is null)");
        }
    }
}

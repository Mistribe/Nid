using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersonalAccessTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "personal_access_token",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    prefix = table.Column<string>(type: "text", nullable: false),
                    token_hash = table.Column<string>(type: "char(64)", nullable: false),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_personal_access_token", x => x.id);
                    table.CheckConstraint("ck_personal_access_token_expiry", "expires_at > created_at");
                    table.CheckConstraint("ck_personal_access_token_scopes", "scopes = '{read}'::text[] or scopes = '{read,write}'::text[] or scopes = '{read,write,write:direct}'::text[]");
                    table.ForeignKey(
                        name: "fk_personal_access_token_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "core",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_personal_access_token_person_id",
                schema: "core",
                table: "personal_access_token",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "ix_personal_access_token_prefix",
                schema: "core",
                table: "personal_access_token",
                column: "prefix",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "personal_access_token",
                schema: "core");
        }
    }
}

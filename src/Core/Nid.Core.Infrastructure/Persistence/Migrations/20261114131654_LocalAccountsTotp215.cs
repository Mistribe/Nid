using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LocalAccountsTotp215 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "totp_enabled_at",
                schema: "core",
                table: "local_credential",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "totp_last_step",
                schema: "core",
                table: "local_credential",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "totp_pending_since",
                schema: "core",
                table: "local_credential",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "totp_secret",
                schema: "core",
                table: "local_credential",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "recovery_code",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "char(64)", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recovery_code", x => x.id);
                    table.ForeignKey(
                        name: "fk_recovery_code_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "core",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_recovery_code_person_id_code_hash",
                schema: "core",
                table: "recovery_code",
                columns: new[] { "person_id", "code_hash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recovery_code",
                schema: "core");

            migrationBuilder.DropColumn(
                name: "totp_enabled_at",
                schema: "core",
                table: "local_credential");

            migrationBuilder.DropColumn(
                name: "totp_last_step",
                schema: "core",
                table: "local_credential");

            migrationBuilder.DropColumn(
                name: "totp_pending_since",
                schema: "core",
                table: "local_credential");

            migrationBuilder.DropColumn(
                name: "totp_secret",
                schema: "core",
                table: "local_credential");
        }
    }
}

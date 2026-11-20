using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApplicationPerAdult382 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "application_id",
                schema: "banking",
                table: "connection",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "application",
                schema: "banking",
                columns: table => new
                {
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<string>(type: "text", nullable: false),
                    private_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    key_loaded_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application", x => x.person_id);
                });

            // Core migrates first, in the same database: existing connections were made with the installation's application.
            migrationBuilder.Sql(
                """
                UPDATE banking.connection SET application_id = (SELECT enable_banking_app_id FROM core.household_settings LIMIT 1)
                WHERE source = 'enable_banking'
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "application",
                schema: "banking");

            migrationBuilder.DropColumn(
                name: "application_id",
                schema: "banking",
                table: "connection");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Documents.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExpectedDocumentDismissals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "expected_dismissal",
                schema: "documents",
                columns: table => new
                {
                    target_kind = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    period = table.Column<int>(type: "integer", nullable: false),
                    dismissed_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dismissed_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_expected_dismissal", x => new { x.target_kind, x.target_id, x.role, x.period });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "expected_dismissal",
                schema: "documents");
        }
    }
}

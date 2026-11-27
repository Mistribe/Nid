using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261127112901_CategoryGuesses450 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "category_guess",
            schema: "banking",
            columns: table => new
            {
                pattern = table.Column<string>(type: "text", nullable: false),
                debit = table.Column<bool>(type: "boolean", nullable: false),
                model = table.Column<string>(type: "text", nullable: false),
                digest = table.Column<string>(type: "text", nullable: false),
                category_id = table.Column<Guid>(type: "uuid", nullable: true),
                probability = table.Column<double>(type: "double precision", nullable: true),
                implementation = table.Column<string>(type: "text", nullable: false),
                version = table.Column<string>(type: "text", nullable: false),
                placement = table.Column<string>(type: "text", nullable: false),
                decided_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_category_guess", x => new { x.pattern, x.debit, x.model, x.digest });
                table.CheckConstraint("ck_category_guess_placement", "placement in ('local','remote')");
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "category_guess",
            schema: "banking");
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Documents.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class DocumentVersion506 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<uint>(
            name: "xmin",
            schema: "documents",
            table: "document",
            type: "xid",
            rowVersion: true,
            nullable: false,
            defaultValue: 0u);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "xmin",
            schema: "documents",
            table: "document");
    }
}

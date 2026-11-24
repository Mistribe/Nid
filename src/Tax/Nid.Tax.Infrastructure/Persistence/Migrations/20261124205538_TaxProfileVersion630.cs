using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Tax.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class TaxProfileVersion630 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<uint>(
            name: "xmin",
            schema: "tax",
            table: "tax_profile",
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
            schema: "tax",
            table: "tax_profile");
    }
}

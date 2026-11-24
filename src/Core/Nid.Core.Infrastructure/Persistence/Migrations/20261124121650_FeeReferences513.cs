using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations;

/// <summary>
/// #513: <c>fee_references</c> leaves Core's model for Investments' settings (<c>investments.settings</c>). Nothing is dropped here:
/// Investments' migration, run after Core's, copies the value and then drops the column; its <c>Down</c> puts it back.
/// </summary>
public partial class FeeReferences513 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}

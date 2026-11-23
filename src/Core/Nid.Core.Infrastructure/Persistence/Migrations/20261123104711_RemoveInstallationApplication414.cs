using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261123104711_RemoveInstallationApplication414 : Migration
{
    /// <summary>
    /// #414 step 2: <c>enable_banking_app_id</c> leaves the model, but not yet the table. Core migrates before Banking, whose
    /// <c>RemoveInstallationApplication414</c> reads the id to end the connections made with it, then drops the column.
    /// </summary>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}

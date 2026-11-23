using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations;

/// <summary>
/// #512: the tax tables leave Core's model for the tax module's (<c>TaxDbContext</c>). Nothing is dropped here: the tax module's first
/// migration, run after Core's, moves <c>core.tax_profile</c>, <c>core.tax_assessment</c> and <c>core.tax_assessment_declarant</c> to the
/// <c>tax</c> schema with their rows, and its <c>Down</c> moves them back.
/// </summary>
public partial class TaxModule512 : Migration
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

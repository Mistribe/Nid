using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Tax.Infrastructure.Persistence.Migrations;

/// <summary>
/// #512, step 1 of 2: the tax module takes over <c>core.tax_profile</c>, <c>core.tax_assessment</c> and
/// <c>core.tax_assessment_declarant</c> as they are. Core's earlier migrations (<c>TaxProfile</c>, <c>TaxAssessments</c>) created them on
/// every database, a fresh one included, and the host runs Core's migrations first: nothing is created, moved or dropped, so the previous
/// image still reads and writes them. Only the <c>tax</c> schema is created, for this module's migrations history. Without the tables
/// the migration fails, so a wrong order never leaves the module without them. Step 2 moves them to <c>tax</c> (#568).
/// </summary>
public partial class InitialTax : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "tax");

        migrationBuilder.Sql("""
            do $$ begin
                if to_regclass('core.tax_profile') is null or to_regclass('core.tax_assessment') is null
                   or to_regclass('core.tax_assessment_declarant') is null then
                    raise exception 'The tax tables are missing from core: run Core''s migrations first (#512).';
                end if;
            end $$;
            """);
    }

    /// <summary>Nothing to undo: the tables were Core's before and stay where they are.</summary>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}

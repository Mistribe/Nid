using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Tax.Infrastructure.Persistence.Migrations;

/// <summary>
/// #568, step 2 of #512: the three tax tables move from <c>core</c> to <c>tax</c> (<c>alter table … set schema</c>: rows, keys, checks and
/// indexes go with them, nothing is copied). Shipped a release after <c>InitialTax</c>, so the previous image already reads them through
/// this module. The declarants' foreign key to <c>core.person</c> is dropped: no module keeps one across schemas (architecture.md §5.3.1).
/// Three views keep the old names in <c>core</c>, <c>xmin</c> included, so the previous image (tables in <c>core</c>) still reads and
/// writes them after an image rollback (§10.1); the next release drops them (#581). <c>Down</c> restores the #566 layout.
/// </summary>
public partial class TaxSchema568 : Migration
{
    // Simple views on one table: Postgres writes through them, and the system column xmin, exposed as a column, still guards an update.
    internal const string CreateViews = """
        create view core.tax_profile as select year, data from tax.tax_profile;
        create view core.tax_assessment as select id, year, data, xmin from tax.tax_assessment;
        create view core.tax_assessment_declarant as select assessment_id, person_id, year, rank from tax.tax_assessment_declarant;
        """;

    internal const string DropViews = """
        drop view core.tax_profile;
        drop view core.tax_assessment;
        drop view core.tax_assessment_declarant;
        """;

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "tax");

        migrationBuilder.DropForeignKey(
            name: "fk_tax_assessment_declarant_person_person_id",
            schema: "core",
            table: "tax_assessment_declarant");

        migrationBuilder.RenameTable(
            name: "tax_profile",
            schema: "core",
            newName: "tax_profile",
            newSchema: "tax");

        migrationBuilder.RenameTable(
            name: "tax_assessment_declarant",
            schema: "core",
            newName: "tax_assessment_declarant",
            newSchema: "tax");

        migrationBuilder.RenameTable(
            name: "tax_assessment",
            schema: "core",
            newName: "tax_assessment",
            newSchema: "tax");

        migrationBuilder.Sql(CreateViews);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(DropViews);

        migrationBuilder.RenameTable(
            name: "tax_profile",
            schema: "tax",
            newName: "tax_profile",
            newSchema: "core");

        migrationBuilder.RenameTable(
            name: "tax_assessment_declarant",
            schema: "tax",
            newName: "tax_assessment_declarant",
            newSchema: "core");

        migrationBuilder.RenameTable(
            name: "tax_assessment",
            schema: "tax",
            newName: "tax_assessment",
            newSchema: "core");

        migrationBuilder.AddForeignKey(
            name: "fk_tax_assessment_declarant_person_person_id",
            schema: "core",
            table: "tax_assessment_declarant",
            column: "person_id",
            principalSchema: "core",
            principalTable: "person",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Tax.Infrastructure.Persistence.Migrations;

/// <summary>
/// #512: the tax module's schema. Its three tables are not created here but moved from <c>core</c>, where Core's earlier migrations
/// (<c>TaxProfile</c>, <c>TaxAssessments</c>) created them on every database, a fresh one included: the host runs Core's migrations
/// first. <c>SET SCHEMA</c> keeps the rows, keys, checks and indexes; nothing is copied. The declarants lose their foreign key to
/// <c>core.person</c>, as no module keeps one across schemas. Without the tables in either schema the migration fails, so a wrong
/// order never leaves the module empty.
/// </summary>
public partial class InitialTax : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "tax");

        migrationBuilder.Sql("""
            alter table if exists core.tax_profile set schema tax;
            alter table if exists core.tax_assessment set schema tax;
            alter table if exists core.tax_assessment_declarant set schema tax;
            do $$ begin
                if to_regclass('tax.tax_profile') is null or to_regclass('tax.tax_assessment') is null
                   or to_regclass('tax.tax_assessment_declarant') is null then
                    raise exception 'The tax tables are in neither core nor tax: run Core''s migrations first (#512).';
                end if;
            end $$;
            alter table tax.tax_assessment_declarant drop constraint if exists fk_tax_assessment_declarant_person_person_id;
            """);
    }

    /// <summary>The way back for an earlier image: the tables return to <c>core</c>, with the declarants' foreign key to the person.</summary>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            alter table tax.tax_assessment_declarant add constraint fk_tax_assessment_declarant_person_person_id
                foreign key (person_id) references core.person (id) on delete restrict;
            alter table tax.tax_profile set schema core;
            alter table tax.tax_assessment set schema core;
            alter table tax.tax_assessment_declarant set schema core;
            """);
    }
}

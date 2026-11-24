using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Investments.Infrastructure.Persistence.Migrations;

/// <summary>
/// #513: the fee references leave <c>core.household_settings</c> for <c>investments.settings</c>. Run after Core's migrations, which no
/// longer map the column (<c>FeeReferences513</c>): its value is copied as is, then the column is dropped, in this release (an image
/// rollback loses it, which the household accepted). A database whose column is already gone still gets its row. <c>Down</c> puts the
/// column back with its value.
/// </summary>
public partial class FeeReferences513 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "settings",
            schema: "investments",
            columns: table => new
            {
                id = table.Column<int>(type: "integer", nullable: false),
                fee_references = table.Column<string>(type: "jsonb", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_settings", x => x.id);
                table.CheckConstraint("ck_settings_single_row", "id = 1");
            });

        migrationBuilder.Sql("""
            insert into investments.settings (id) values (1);
            do $$ begin
                if exists (select 1 from information_schema.columns
                           where table_schema = 'core' and table_name = 'household_settings' and column_name = 'fee_references') then
                    update investments.settings s set fee_references = h.fee_references
                    from core.household_settings h where h.id = 1 and s.id = 1;
                    alter table core.household_settings drop column fee_references;
                end if;
            end $$;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            alter table core.household_settings add column fee_references jsonb;
            update core.household_settings h set fee_references = s.fee_references from investments.settings s where h.id = 1 and s.id = 1;
            """);

        migrationBuilder.DropTable(
            name: "settings",
            schema: "investments");
    }
}

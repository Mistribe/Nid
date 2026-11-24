using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations;

/// <summary>
/// #513: Banking's settings leave <c>core.household_settings</c> for <c>banking.settings</c>. Run after Core's migrations, which no longer
/// map the two columns (<c>BankingSettings513</c>): their values are copied, then the columns are dropped with their check, in this
/// release (an image rollback loses them, which the household accepted). A database whose columns are already gone still gets its row.
/// <c>Down</c> puts the columns back with their values.
/// </summary>
public partial class BankingSettings513 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "settings",
            schema: "banking",
            columns: table => new
            {
                id = table.Column<int>(type: "integer", nullable: false),
                enable_banking_app_id = table.Column<string>(type: "text", nullable: true),
                bank_retention_months = table.Column<int>(type: "integer", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_settings", x => x.id);
                table.CheckConstraint("ck_settings_bank_retention", "bank_retention_months between 1 and 600");
                table.CheckConstraint("ck_settings_single_row", "id = 1");
            });

        // Dropping a column drops the single-column check on it (ck_household_settings_bank_retention).
        migrationBuilder.Sql("""
            insert into banking.settings (id) values (1);
            do $$ begin
                if exists (select 1 from information_schema.columns
                           where table_schema = 'core' and table_name = 'household_settings' and column_name = 'bank_retention_months') then
                    update banking.settings s set enable_banking_app_id = h.enable_banking_app_id, bank_retention_months = h.bank_retention_months
                    from core.household_settings h where h.id = 1 and s.id = 1;
                    alter table core.household_settings drop column enable_banking_app_id, drop column bank_retention_months;
                end if;
            end $$;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            alter table core.household_settings add column enable_banking_app_id text, add column bank_retention_months integer,
                add constraint ck_household_settings_bank_retention check (bank_retention_months between 1 and 600);
            update core.household_settings h set enable_banking_app_id = s.enable_banking_app_id, bank_retention_months = s.bank_retention_months
            from banking.settings s where h.id = 1 and s.id = 1;
            """);

        migrationBuilder.DropTable(
            name: "settings",
            schema: "banking");
    }
}

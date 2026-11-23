using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveInstallationApplication414 : Migration
    {
        /// <summary>
        /// #414 step 2: the installation's Enable Banking application goes. Its connections that were never transferred to an adult get its
        /// id (the legacy ones had none), then every connection made with an application that is not its creator's own ends as the transfer
        /// ends them (<c>revoked</c>, <c>banking.application_transferred</c>: its creator is told to link the bank again); a pending one is
        /// deleted. Their sessions lapse at Enable Banking: no key is left to delete them with. The household's id goes once read: Core
        /// migrates first, so its own migration leaves the column for this one.
        /// </summary>
        public const string EndInstallationConnections = """
            do $$
            begin
                -- A database Core never migrated (a module's own test) has no such column.
                if exists (select 1 from information_schema.columns
                           where table_schema = 'core' and table_name = 'household_settings' and column_name = 'enable_banking_app_id') then
                    update banking.connection c set application_id = s.enable_banking_app_id
                    from core.household_settings s
                    where c.application_id is null and c.source <> 'file' and s.enable_banking_app_id is not null;
                    alter table core.household_settings drop column enable_banking_app_id;
                end if;
            end $$;

            delete from banking.connection c
            where c.status = 'pending' and c.source <> 'file'
              and not exists (select 1 from banking.application a where a.person_id = c.person_id and a.application_id = c.application_id);

            update banking.connection c set status = 'revoked', last_error_code = 'banking.application_transferred'
            where c.status in ('active', 'error', 'expired') and c.source <> 'file'
              and not exists (select 1 from banking.application a where a.person_id = c.person_id and a.application_id = c.application_id);
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(EndInstallationConnections);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.Sql("""
                do $$
                begin
                    if to_regclass('core.household_settings') is not null then
                        alter table core.household_settings add column if not exists enable_banking_app_id text;
                    end if;
                end $$;
                """);
    }
}

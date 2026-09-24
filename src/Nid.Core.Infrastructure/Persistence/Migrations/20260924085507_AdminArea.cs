using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminArea : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_person_subject",
                schema: "core",
                table: "person");

            migrationBuilder.AddColumn<bool>(
                name: "is_admin",
                schema: "core",
                table: "person",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "max_adults",
                schema: "core",
                table: "household_settings",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddCheckConstraint(
                name: "ck_person_admin",
                schema: "core",
                table: "person",
                sql: "kind = 'adult' or not is_admin");

            migrationBuilder.AddCheckConstraint(
                name: "ck_person_subject",
                schema: "core",
                table: "person",
                sql: "(kind = 'adult' and (oidc_subject is not null or email is not null)) or (kind = 'dependent' and oidc_subject is null)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_household_settings_max_adults",
                schema: "core",
                table: "household_settings",
                sql: "max_adults >= 1");

            migrationBuilder.Sql(
                "create unique index ix_person_adult_email on core.person (lower(email)) where kind = 'adult' and email is not null;");

            // The first person to sign in is admin (administration-and-audit.md §2.1); for a household that already
            // exists, that is its oldest adult.
            migrationBuilder.Sql("""
                update core.person set is_admin = true
                where id = (select id from core.person where kind = 'adult' order by created_at, id limit 1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("drop index core.ix_person_adult_email;");

            migrationBuilder.DropCheckConstraint(
                name: "ck_person_admin",
                schema: "core",
                table: "person");

            migrationBuilder.DropCheckConstraint(
                name: "ck_person_subject",
                schema: "core",
                table: "person");

            migrationBuilder.DropCheckConstraint(
                name: "ck_household_settings_max_adults",
                schema: "core",
                table: "household_settings");

            migrationBuilder.DropColumn(
                name: "is_admin",
                schema: "core",
                table: "person");

            migrationBuilder.DropColumn(
                name: "max_adults",
                schema: "core",
                table: "household_settings");

            migrationBuilder.AddCheckConstraint(
                name: "ck_person_subject",
                schema: "core",
                table: "person",
                sql: "(kind = 'adult' and oidc_subject is not null) or (kind = 'dependent' and oidc_subject is null)");
        }
    }
}

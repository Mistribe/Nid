using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Investments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnvelopeClosingAndImport : Migration
    {
        /// <summary>2B1b spec §3: the date of the closing value of 0, else the archive's day in the household's time zone.</summary>
        public const string Backfill = """
            update investments.envelope e
            set closed_on = coalesce(
                (select max(v.as_of) from core.valuation v where v.item_id = e.item_id and v.retired_at is null and v.amount = 0),
                (i.archived_at at time zone coalesce((select s.timezone from core.household_settings s limit 1), 'Europe/Paris'))::date)
            from core.item i
            where i.id = e.item_id and i.archived_at is not null and e.closed_on is null
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<LocalDate>(
                name: "closed_on",
                schema: "investments",
                table: "envelope",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_hash",
                schema: "investments",
                table: "envelope",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_ref",
                schema: "investments",
                table: "envelope",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "imported_by",
                schema: "investments",
                table: "envelope",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_envelope_imported_by_import_ref",
                schema: "investments",
                table: "envelope",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_envelope_import",
                schema: "investments",
                table: "envelope",
                sql: "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");

            migrationBuilder.Sql(Backfill);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_envelope_imported_by_import_ref",
                schema: "investments",
                table: "envelope");

            migrationBuilder.DropCheckConstraint(
                name: "ck_envelope_import",
                schema: "investments",
                table: "envelope");

            migrationBuilder.DropColumn(
                name: "closed_on",
                schema: "investments",
                table: "envelope");

            migrationBuilder.DropColumn(
                name: "import_hash",
                schema: "investments",
                table: "envelope");

            migrationBuilder.DropColumn(
                name: "import_ref",
                schema: "investments",
                table: "envelope");

            migrationBuilder.DropColumn(
                name: "imported_by",
                schema: "investments",
                table: "envelope");
        }
    }
}

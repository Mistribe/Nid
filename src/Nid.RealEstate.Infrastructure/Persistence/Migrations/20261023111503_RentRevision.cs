using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.RealEstate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RentRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "indexed",
                schema: "realestate",
                table: "property_tenancy",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "irl_reference_quarter",
                schema: "realestate",
                table: "property_tenancy",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "revised_from",
                schema: "realestate",
                table: "property_tenancy",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "energy_class",
                schema: "realestate",
                table: "property",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "irl",
                schema: "realestate",
                columns: table => new
                {
                    quarter = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<decimal>(type: "numeric(10,4)", nullable: false),
                    published_on = table.Column<LocalDate>(type: "date", nullable: true),
                    fetched_on = table.Column<LocalDate>(type: "date", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_irl", x => x.quarter);
                    table.CheckConstraint("ck_irl_quarter", "quarter ~ '^[0-9]{4}-Q[1-4]$'");
                    table.CheckConstraint("ck_irl_source", "source in ('insee','manual')");
                    table.CheckConstraint("ck_irl_value", "value > 0");
                });

            migrationBuilder.CreateTable(
                name: "irl_run",
                schema: "realestate",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    added = table.Column<int>(type: "integer", nullable: false),
                    replaced = table.Column<int>(type: "integer", nullable: false),
                    rejected = table.Column<int>(type: "integer", nullable: false),
                    message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_irl_run", x => x.id);
                    table.CheckConstraint("ck_irl_run_status", "status in ('running','succeeded','failed')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_property_tenancy_revised_from",
                schema: "realestate",
                table: "property_tenancy",
                column: "revised_from");

            migrationBuilder.AddCheckConstraint(
                name: "ck_property_tenancy_indexed",
                schema: "realestate",
                table: "property_tenancy",
                sql: "not indexed or irl_reference_quarter is not null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_property_tenancy_irl_quarter",
                schema: "realestate",
                table: "property_tenancy",
                sql: "irl_reference_quarter is null or irl_reference_quarter between 1 and 4");

            migrationBuilder.AddCheckConstraint(
                name: "ck_property_energy_class",
                schema: "realestate",
                table: "property",
                sql: "energy_class is null or energy_class in ('A','B','C','D','E','F','G')");

            migrationBuilder.CreateIndex(
                name: "ix_irl_run_started_at",
                schema: "realestate",
                table: "irl_run",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_irl_run_status",
                schema: "realestate",
                table: "irl_run",
                column: "status",
                unique: true,
                filter: "status = 'running'");

            migrationBuilder.AddForeignKey(
                name: "fk_property_tenancy_property_tenancy_revised_from",
                schema: "realestate",
                table: "property_tenancy",
                column: "revised_from",
                principalSchema: "realestate",
                principalTable: "property_tenancy",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_property_tenancy_property_tenancy_revised_from",
                schema: "realestate",
                table: "property_tenancy");

            migrationBuilder.DropTable(
                name: "irl",
                schema: "realestate");

            migrationBuilder.DropTable(
                name: "irl_run",
                schema: "realestate");

            migrationBuilder.DropIndex(
                name: "ix_property_tenancy_revised_from",
                schema: "realestate",
                table: "property_tenancy");

            migrationBuilder.DropCheckConstraint(
                name: "ck_property_tenancy_indexed",
                schema: "realestate",
                table: "property_tenancy");

            migrationBuilder.DropCheckConstraint(
                name: "ck_property_tenancy_irl_quarter",
                schema: "realestate",
                table: "property_tenancy");

            migrationBuilder.DropCheckConstraint(
                name: "ck_property_energy_class",
                schema: "realestate",
                table: "property");

            migrationBuilder.DropColumn(
                name: "indexed",
                schema: "realestate",
                table: "property_tenancy");

            migrationBuilder.DropColumn(
                name: "irl_reference_quarter",
                schema: "realestate",
                table: "property_tenancy");

            migrationBuilder.DropColumn(
                name: "revised_from",
                schema: "realestate",
                table: "property_tenancy");

            migrationBuilder.DropColumn(
                name: "energy_class",
                schema: "realestate",
                table: "property");
        }
    }
}

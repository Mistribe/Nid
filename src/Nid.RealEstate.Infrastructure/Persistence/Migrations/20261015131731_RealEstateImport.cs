using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.RealEstate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RealEstateImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "import_hash",
                schema: "realestate",
                table: "property_tenancy",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_ref",
                schema: "realestate",
                table: "property_tenancy",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "imported_by",
                schema: "realestate",
                table: "property_tenancy",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_hash",
                schema: "realestate",
                table: "property_recurring_cost",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_ref",
                schema: "realestate",
                table: "property_recurring_cost",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "imported_by",
                schema: "realestate",
                table: "property_recurring_cost",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_hash",
                schema: "realestate",
                table: "property",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_ref",
                schema: "realestate",
                table: "property",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "imported_by",
                schema: "realestate",
                table: "property",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_hash",
                schema: "realestate",
                table: "loan_phase",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_ref",
                schema: "realestate",
                table: "loan_phase",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "imported_by",
                schema: "realestate",
                table: "loan_phase",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_hash",
                schema: "realestate",
                table: "loan_insurance",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_ref",
                schema: "realestate",
                table: "loan_insurance",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "imported_by",
                schema: "realestate",
                table: "loan_insurance",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_hash",
                schema: "realestate",
                table: "loan_fee",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_ref",
                schema: "realestate",
                table: "loan_fee",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "imported_by",
                schema: "realestate",
                table: "loan_fee",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_hash",
                schema: "realestate",
                table: "loan_event",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_ref",
                schema: "realestate",
                table: "loan_event",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "imported_by",
                schema: "realestate",
                table: "loan_event",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_hash",
                schema: "realestate",
                table: "loan",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_ref",
                schema: "realestate",
                table: "loan",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "imported_by",
                schema: "realestate",
                table: "loan",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_property_tenancy_imported_by_import_ref",
                schema: "realestate",
                table: "property_tenancy",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_property_tenancy_import",
                schema: "realestate",
                table: "property_tenancy",
                sql: "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");

            migrationBuilder.CreateIndex(
                name: "ix_property_recurring_cost_imported_by_import_ref",
                schema: "realestate",
                table: "property_recurring_cost",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_property_recurring_cost_import",
                schema: "realestate",
                table: "property_recurring_cost",
                sql: "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");

            migrationBuilder.CreateIndex(
                name: "ix_property_imported_by_import_ref",
                schema: "realestate",
                table: "property",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_property_import",
                schema: "realestate",
                table: "property",
                sql: "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");

            migrationBuilder.CreateIndex(
                name: "ix_loan_phase_imported_by_import_ref",
                schema: "realestate",
                table: "loan_phase",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loan_phase_import",
                schema: "realestate",
                table: "loan_phase",
                sql: "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");

            migrationBuilder.CreateIndex(
                name: "ix_loan_insurance_imported_by_import_ref",
                schema: "realestate",
                table: "loan_insurance",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loan_insurance_import",
                schema: "realestate",
                table: "loan_insurance",
                sql: "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");

            migrationBuilder.CreateIndex(
                name: "ix_loan_fee_imported_by_import_ref",
                schema: "realestate",
                table: "loan_fee",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loan_fee_import",
                schema: "realestate",
                table: "loan_fee",
                sql: "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");

            migrationBuilder.CreateIndex(
                name: "ix_loan_event_imported_by_import_ref",
                schema: "realestate",
                table: "loan_event",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loan_event_import",
                schema: "realestate",
                table: "loan_event",
                sql: "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");

            migrationBuilder.CreateIndex(
                name: "ix_loan_imported_by_import_ref",
                schema: "realestate",
                table: "loan",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loan_import",
                schema: "realestate",
                table: "loan",
                sql: "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_property_tenancy_imported_by_import_ref",
                schema: "realestate",
                table: "property_tenancy");

            migrationBuilder.DropCheckConstraint(
                name: "ck_property_tenancy_import",
                schema: "realestate",
                table: "property_tenancy");

            migrationBuilder.DropIndex(
                name: "ix_property_recurring_cost_imported_by_import_ref",
                schema: "realestate",
                table: "property_recurring_cost");

            migrationBuilder.DropCheckConstraint(
                name: "ck_property_recurring_cost_import",
                schema: "realestate",
                table: "property_recurring_cost");

            migrationBuilder.DropIndex(
                name: "ix_property_imported_by_import_ref",
                schema: "realestate",
                table: "property");

            migrationBuilder.DropCheckConstraint(
                name: "ck_property_import",
                schema: "realestate",
                table: "property");

            migrationBuilder.DropIndex(
                name: "ix_loan_phase_imported_by_import_ref",
                schema: "realestate",
                table: "loan_phase");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loan_phase_import",
                schema: "realestate",
                table: "loan_phase");

            migrationBuilder.DropIndex(
                name: "ix_loan_insurance_imported_by_import_ref",
                schema: "realestate",
                table: "loan_insurance");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loan_insurance_import",
                schema: "realestate",
                table: "loan_insurance");

            migrationBuilder.DropIndex(
                name: "ix_loan_fee_imported_by_import_ref",
                schema: "realestate",
                table: "loan_fee");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loan_fee_import",
                schema: "realestate",
                table: "loan_fee");

            migrationBuilder.DropIndex(
                name: "ix_loan_event_imported_by_import_ref",
                schema: "realestate",
                table: "loan_event");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loan_event_import",
                schema: "realestate",
                table: "loan_event");

            migrationBuilder.DropIndex(
                name: "ix_loan_imported_by_import_ref",
                schema: "realestate",
                table: "loan");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loan_import",
                schema: "realestate",
                table: "loan");

            migrationBuilder.DropColumn(
                name: "import_hash",
                schema: "realestate",
                table: "property_tenancy");

            migrationBuilder.DropColumn(
                name: "import_ref",
                schema: "realestate",
                table: "property_tenancy");

            migrationBuilder.DropColumn(
                name: "imported_by",
                schema: "realestate",
                table: "property_tenancy");

            migrationBuilder.DropColumn(
                name: "import_hash",
                schema: "realestate",
                table: "property_recurring_cost");

            migrationBuilder.DropColumn(
                name: "import_ref",
                schema: "realestate",
                table: "property_recurring_cost");

            migrationBuilder.DropColumn(
                name: "imported_by",
                schema: "realestate",
                table: "property_recurring_cost");

            migrationBuilder.DropColumn(
                name: "import_hash",
                schema: "realestate",
                table: "property");

            migrationBuilder.DropColumn(
                name: "import_ref",
                schema: "realestate",
                table: "property");

            migrationBuilder.DropColumn(
                name: "imported_by",
                schema: "realestate",
                table: "property");

            migrationBuilder.DropColumn(
                name: "import_hash",
                schema: "realestate",
                table: "loan_phase");

            migrationBuilder.DropColumn(
                name: "import_ref",
                schema: "realestate",
                table: "loan_phase");

            migrationBuilder.DropColumn(
                name: "imported_by",
                schema: "realestate",
                table: "loan_phase");

            migrationBuilder.DropColumn(
                name: "import_hash",
                schema: "realestate",
                table: "loan_insurance");

            migrationBuilder.DropColumn(
                name: "import_ref",
                schema: "realestate",
                table: "loan_insurance");

            migrationBuilder.DropColumn(
                name: "imported_by",
                schema: "realestate",
                table: "loan_insurance");

            migrationBuilder.DropColumn(
                name: "import_hash",
                schema: "realestate",
                table: "loan_fee");

            migrationBuilder.DropColumn(
                name: "import_ref",
                schema: "realestate",
                table: "loan_fee");

            migrationBuilder.DropColumn(
                name: "imported_by",
                schema: "realestate",
                table: "loan_fee");

            migrationBuilder.DropColumn(
                name: "import_hash",
                schema: "realestate",
                table: "loan_event");

            migrationBuilder.DropColumn(
                name: "import_ref",
                schema: "realestate",
                table: "loan_event");

            migrationBuilder.DropColumn(
                name: "imported_by",
                schema: "realestate",
                table: "loan_event");

            migrationBuilder.DropColumn(
                name: "import_hash",
                schema: "realestate",
                table: "loan");

            migrationBuilder.DropColumn(
                name: "import_ref",
                schema: "realestate",
                table: "loan");

            migrationBuilder.DropColumn(
                name: "imported_by",
                schema: "realestate",
                table: "loan");
        }
    }
}

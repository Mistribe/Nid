using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Subscriptions.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImportedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Import provenance privacy (spec 2026-09-25 §3): no backfill — who imported an existing row is not known.
            migrationBuilder.DropIndex(
                name: "ix_subscription_import_ref",
                schema: "subscriptions",
                table: "subscription");

            migrationBuilder.DropIndex(
                name: "ix_purchase_import_ref",
                schema: "subscriptions",
                table: "purchase");

            migrationBuilder.DropIndex(
                name: "ix_billing_phase_import_ref",
                schema: "subscriptions",
                table: "billing_phase");

            migrationBuilder.AddColumn<Guid>(
                name: "imported_by",
                schema: "subscriptions",
                table: "subscription",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "imported_by",
                schema: "subscriptions",
                table: "purchase",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "imported_by",
                schema: "subscriptions",
                table: "billing_phase",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscription_imported_by_import_ref",
                schema: "subscriptions",
                table: "subscription",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_subscription_imported_by",
                schema: "subscriptions",
                table: "subscription",
                sql: "imported_by is null or import_ref is not null");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_imported_by_import_ref",
                schema: "subscriptions",
                table: "purchase",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_purchase_imported_by",
                schema: "subscriptions",
                table: "purchase",
                sql: "imported_by is null or import_ref is not null");

            migrationBuilder.CreateIndex(
                name: "ix_billing_phase_imported_by_import_ref",
                schema: "subscriptions",
                table: "billing_phase",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_billing_phase_imported_by",
                schema: "subscriptions",
                table: "billing_phase",
                sql: "imported_by is null or import_ref is not null");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_subscription_imported_by_import_ref",
                schema: "subscriptions",
                table: "subscription");

            migrationBuilder.DropCheckConstraint(
                name: "ck_subscription_imported_by",
                schema: "subscriptions",
                table: "subscription");

            migrationBuilder.DropIndex(
                name: "ix_purchase_imported_by_import_ref",
                schema: "subscriptions",
                table: "purchase");

            migrationBuilder.DropCheckConstraint(
                name: "ck_purchase_imported_by",
                schema: "subscriptions",
                table: "purchase");

            migrationBuilder.DropIndex(
                name: "ix_billing_phase_imported_by_import_ref",
                schema: "subscriptions",
                table: "billing_phase");

            migrationBuilder.DropCheckConstraint(
                name: "ck_billing_phase_imported_by",
                schema: "subscriptions",
                table: "billing_phase");

            migrationBuilder.DropColumn(
                name: "imported_by",
                schema: "subscriptions",
                table: "subscription");

            migrationBuilder.DropColumn(
                name: "imported_by",
                schema: "subscriptions",
                table: "purchase");

            migrationBuilder.DropColumn(
                name: "imported_by",
                schema: "subscriptions",
                table: "billing_phase");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_import_ref",
                schema: "subscriptions",
                table: "subscription",
                column: "import_ref",
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_import_ref",
                schema: "subscriptions",
                table: "purchase",
                column: "import_ref",
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.CreateIndex(
                name: "ix_billing_phase_import_ref",
                schema: "subscriptions",
                table: "billing_phase",
                column: "import_ref",
                unique: true,
                filter: "import_ref is not null");
        }
    }
}

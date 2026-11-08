using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Subscriptions.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImportRefs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "import_hash",
                schema: "subscriptions",
                table: "subscription",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_ref",
                schema: "subscriptions",
                table: "subscription",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_hash",
                schema: "subscriptions",
                table: "purchase",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_ref",
                schema: "subscriptions",
                table: "purchase",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_hash",
                schema: "subscriptions",
                table: "billing_phase",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_ref",
                schema: "subscriptions",
                table: "billing_phase",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscription_import_ref",
                schema: "subscriptions",
                table: "subscription",
                column: "import_ref",
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_subscription_import",
                schema: "subscriptions",
                table: "subscription",
                sql: "(import_ref is null) = (import_hash is null)");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_import_ref",
                schema: "subscriptions",
                table: "purchase",
                column: "import_ref",
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_purchase_import",
                schema: "subscriptions",
                table: "purchase",
                sql: "(import_ref is null) = (import_hash is null)");

            migrationBuilder.CreateIndex(
                name: "ix_billing_phase_import_ref",
                schema: "subscriptions",
                table: "billing_phase",
                column: "import_ref",
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_billing_phase_import",
                schema: "subscriptions",
                table: "billing_phase",
                sql: "(import_ref is null) = (import_hash is null)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_subscription_import_ref",
                schema: "subscriptions",
                table: "subscription");

            migrationBuilder.DropCheckConstraint(
                name: "ck_subscription_import",
                schema: "subscriptions",
                table: "subscription");

            migrationBuilder.DropIndex(
                name: "ix_purchase_import_ref",
                schema: "subscriptions",
                table: "purchase");

            migrationBuilder.DropCheckConstraint(
                name: "ck_purchase_import",
                schema: "subscriptions",
                table: "purchase");

            migrationBuilder.DropIndex(
                name: "ix_billing_phase_import_ref",
                schema: "subscriptions",
                table: "billing_phase");

            migrationBuilder.DropCheckConstraint(
                name: "ck_billing_phase_import",
                schema: "subscriptions",
                table: "billing_phase");

            migrationBuilder.DropColumn(
                name: "import_hash",
                schema: "subscriptions",
                table: "subscription");

            migrationBuilder.DropColumn(
                name: "import_ref",
                schema: "subscriptions",
                table: "subscription");

            migrationBuilder.DropColumn(
                name: "import_hash",
                schema: "subscriptions",
                table: "purchase");

            migrationBuilder.DropColumn(
                name: "import_ref",
                schema: "subscriptions",
                table: "purchase");

            migrationBuilder.DropColumn(
                name: "import_hash",
                schema: "subscriptions",
                table: "billing_phase");

            migrationBuilder.DropColumn(
                name: "import_ref",
                schema: "subscriptions",
                table: "billing_phase");
        }
    }
}

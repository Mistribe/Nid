using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Subscriptions.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubscriptionInsurance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "subscription_insurance",
                schema: "subscriptions",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: true),
                    insured_label = table.Column<string>(type: "text", nullable: true),
                    policy_number = table.Column<string>(type: "text", nullable: true),
                    insurer_contact = table.Column<string>(type: "text", nullable: true),
                    import_ref = table.Column<string>(type: "text", nullable: true),
                    import_hash = table.Column<string>(type: "char(64)", nullable: true),
                    imported_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_insurance", x => x.item_id);
                    table.CheckConstraint("ck_subscription_insurance_import", "(import_ref is null) = (import_hash is null)");
                    table.CheckConstraint("ck_subscription_insurance_imported_by", "imported_by is null or import_ref is not null");
                    table.CheckConstraint("ck_subscription_insurance_kind", "kind in ('home','car','health','provident','other')");
                    table.CheckConstraint("ck_subscription_insurance_text", "char_length(insured_label) <= 200 and char_length(policy_number) <= 200 and char_length(insurer_contact) <= 200");
                    table.ForeignKey(
                        name: "fk_subscription_insurance_subscription_item_id",
                        column: x => x.item_id,
                        principalSchema: "subscriptions",
                        principalTable: "subscription",
                        principalColumn: "item_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "insurance_guarantee",
                schema: "subscriptions",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    ceiling = table.Column<string>(type: "text", nullable: true),
                    deductible = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_insurance_guarantee", x => new { x.item_id, x.position });
                    table.CheckConstraint("ck_insurance_guarantee_position", "\"position\" >= 0");
                    table.CheckConstraint("ck_insurance_guarantee_text", "char_length(label) between 1 and 200 and char_length(ceiling) <= 200 and char_length(deductible) <= 200");
                    table.ForeignKey(
                        name: "fk_insurance_guarantee_subscription_insurance_item_id",
                        column: x => x.item_id,
                        principalSchema: "subscriptions",
                        principalTable: "subscription_insurance",
                        principalColumn: "item_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_subscription_insurance_imported_by_import_ref",
                schema: "subscriptions",
                table: "subscription_insurance",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "insurance_guarantee",
                schema: "subscriptions");

            migrationBuilder.DropTable(
                name: "subscription_insurance",
                schema: "subscriptions");
        }
    }
}

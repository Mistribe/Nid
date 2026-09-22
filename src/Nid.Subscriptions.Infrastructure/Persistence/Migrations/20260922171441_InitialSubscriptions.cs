using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Subscriptions.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "subscriptions");

            migrationBuilder.CreateTable(
                name: "purchase",
                schema: "subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<LocalDate>(type: "date", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    price_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    price_currency = table.Column<string>(type: "char(3)", nullable: false),
                    payer_type = table.Column<string>(type: "text", nullable: false),
                    payer_person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    allocated_amount = table.Column<decimal>(type: "numeric", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_purchase", x => x.id);
                    table.CheckConstraint("ck_purchase_allocated", "allocated_amount is null or (kind = 'bundle' and allocated_amount >= 0 and allocated_amount <= price_amount)");
                    table.CheckConstraint("ck_purchase_kind", "kind in ('prepaid_term','bundle','gift','adjustment','refund')");
                    table.CheckConstraint("ck_purchase_price_sign", "price_amount >= 0 or kind in ('adjustment','refund')");
                });

            migrationBuilder.CreateTable(
                name: "subscription",
                schema: "subscriptions",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_key = table.Column<string>(type: "text", nullable: true),
                    auto_renew = table.Column<bool>(type: "boolean", nullable: false),
                    cancelled_at = table.Column<LocalDate>(type: "date", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription", x => x.item_id);
                });

            migrationBuilder.CreateTable(
                name: "coverage_grant",
                schema: "subscriptions",
                columns: table => new
                {
                    purchase_id = table.Column<Guid>(type: "uuid", nullable: false),
                    index = table.Column<int>(type: "integer", nullable: false),
                    subscription_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    duration_count = table.Column<int>(type: "integer", nullable: false),
                    duration_unit = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    starts_on = table.Column<LocalDate>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coverage_grant", x => new { x.purchase_id, x.index });
                    table.CheckConstraint("ck_coverage_grant_duration", "duration_count > 0");
                    table.CheckConstraint("ck_coverage_grant_source", "source in ('paid','bonus','included','trial','gift')");
                    table.ForeignKey(
                        name: "fk_coverage_grant_purchase_purchase_id",
                        column: x => x.purchase_id,
                        principalSchema: "subscriptions",
                        principalTable: "purchase",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "billing_phase",
                schema: "subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_on = table.Column<LocalDate>(type: "date", nullable: false),
                    ends_on = table.Column<LocalDate>(type: "date", nullable: true),
                    price_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    price_currency = table.Column<string>(type: "char(3)", nullable: false),
                    every_count = table.Column<int>(type: "integer", nullable: false),
                    every_unit = table.Column<string>(type: "text", nullable: false),
                    covers_count = table.Column<int>(type: "integer", nullable: true),
                    covers_unit = table.Column<string>(type: "text", nullable: true),
                    max_cycles = table.Column<int>(type: "integer", nullable: true),
                    payer_type = table.Column<string>(type: "text", nullable: false),
                    payer_person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_phase", x => x.id);
                    table.CheckConstraint("ck_billing_phase_every", "every_count > 0");
                    table.CheckConstraint("ck_billing_phase_payer", "(payer_type = 'person' and payer_person_id is not null) or (payer_type <> 'person' and payer_person_id is null)");
                    table.CheckConstraint("ck_billing_phase_range", "ends_on is null or ends_on > starts_on");
                    table.ForeignKey(
                        name: "fk_billing_phase_subscription_item_id",
                        column: x => x.item_id,
                        principalSchema: "subscriptions",
                        principalTable: "subscription",
                        principalColumn: "item_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "subscription_user",
                schema: "subscriptions",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_user", x => new { x.item_id, x.person_id });
                    table.ForeignKey(
                        name: "fk_subscription_user_subscription_item_id",
                        column: x => x.item_id,
                        principalSchema: "subscriptions",
                        principalTable: "subscription",
                        principalColumn: "item_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_billing_phase_item_id",
                schema: "subscriptions",
                table: "billing_phase",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ix_coverage_grant_subscription_item_id",
                schema: "subscriptions",
                table: "coverage_grant",
                column: "subscription_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_date",
                schema: "subscriptions",
                table: "purchase",
                column: "date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_phase",
                schema: "subscriptions");

            migrationBuilder.DropTable(
                name: "coverage_grant",
                schema: "subscriptions");

            migrationBuilder.DropTable(
                name: "subscription_user",
                schema: "subscriptions");

            migrationBuilder.DropTable(
                name: "purchase",
                schema: "subscriptions");

            migrationBuilder.DropTable(
                name: "subscription",
                schema: "subscriptions");
        }
    }
}

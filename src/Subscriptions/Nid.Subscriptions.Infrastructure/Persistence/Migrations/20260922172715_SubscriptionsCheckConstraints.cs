using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Subscriptions.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubscriptionsCheckConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_purchase_payer_type",
                schema: "subscriptions",
                table: "purchase",
                sql: "payer_type in ('person','household','external')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_billing_phase_payer_type",
                schema: "subscriptions",
                table: "billing_phase",
                sql: "payer_type in ('person','household')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_billing_phase_price_sign",
                schema: "subscriptions",
                table: "billing_phase",
                sql: "price_amount >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_purchase_payer_type",
                schema: "subscriptions",
                table: "purchase");

            migrationBuilder.DropCheckConstraint(
                name: "ck_billing_phase_payer_type",
                schema: "subscriptions",
                table: "billing_phase");

            migrationBuilder.DropCheckConstraint(
                name: "ck_billing_phase_price_sign",
                schema: "subscriptions",
                table: "billing_phase");
        }
    }
}

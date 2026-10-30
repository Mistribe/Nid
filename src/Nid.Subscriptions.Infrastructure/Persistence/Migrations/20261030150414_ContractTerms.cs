using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Subscriptions.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContractTerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<LocalDate>(
                name: "anniversary_on",
                schema: "subscriptions",
                table: "subscription",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<LocalDate>(
                name: "cancellable_from",
                schema: "subscriptions",
                table: "subscription",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<LocalDate>(
                name: "commitment_ends_on",
                schema: "subscriptions",
                table: "subscription",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "notice_count",
                schema: "subscriptions",
                table: "subscription",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "notice_unit",
                schema: "subscriptions",
                table: "subscription",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_subscription_notice",
                schema: "subscriptions",
                table: "subscription",
                sql: "(notice_count is null) = (notice_unit is null) and (notice_count is null or notice_count > 0) and (notice_unit is null or notice_unit in ('day','week','month','year'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_subscription_notice",
                schema: "subscriptions",
                table: "subscription");

            migrationBuilder.DropColumn(
                name: "anniversary_on",
                schema: "subscriptions",
                table: "subscription");

            migrationBuilder.DropColumn(
                name: "cancellable_from",
                schema: "subscriptions",
                table: "subscription");

            migrationBuilder.DropColumn(
                name: "commitment_ends_on",
                schema: "subscriptions",
                table: "subscription");

            migrationBuilder.DropColumn(
                name: "notice_count",
                schema: "subscriptions",
                table: "subscription");

            migrationBuilder.DropColumn(
                name: "notice_unit",
                schema: "subscriptions",
                table: "subscription");
        }
    }
}

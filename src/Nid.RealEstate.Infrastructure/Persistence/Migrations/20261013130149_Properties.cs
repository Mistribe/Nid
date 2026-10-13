using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.RealEstate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Properties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "property_item_id",
                schema: "realestate",
                table: "loan",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "property",
                schema: "realestate",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_type = table.Column<string>(type: "text", nullable: false),
                    purchase_date = table.Column<LocalDate>(type: "date", nullable: false),
                    purchase_price = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    notary_fees = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    agency_fees = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    other_acquisition_costs = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    surface_m2 = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    location_label = table.Column<string>(type: "text", nullable: true),
                    valuation_cadence = table.Column<string>(type: "text", nullable: false),
                    stale_after_days = table.Column<int>(type: "integer", nullable: true),
                    rental_starts_on = table.Column<LocalDate>(type: "date", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_property", x => x.item_id);
                    table.CheckConstraint("ck_property_cadence", "valuation_cadence in ('yearly','manual')");
                    table.CheckConstraint("ck_property_fees", "notary_fees >= 0 and agency_fees >= 0 and other_acquisition_costs >= 0");
                    table.CheckConstraint("ck_property_purchase_price", "purchase_price > 0");
                    table.CheckConstraint("ck_property_rental_starts_on", "rental_starts_on is null or rental_starts_on >= purchase_date");
                    table.CheckConstraint("ck_property_stale_after_days", "stale_after_days is null or stale_after_days between 1 and 3650");
                    table.CheckConstraint("ck_property_surface", "surface_m2 is null or surface_m2 > 0");
                    table.CheckConstraint("ck_property_type", "property_type in ('primary_residence','secondary_residence','rental','parking','land','other')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_loan_property_item_id",
                schema: "realestate",
                table: "loan",
                column: "property_item_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "property",
                schema: "realestate");

            migrationBuilder.DropIndex(
                name: "ix_loan_property_item_id",
                schema: "realestate",
                table: "loan");

            migrationBuilder.DropColumn(
                name: "property_item_id",
                schema: "realestate",
                table: "loan");
        }
    }
}

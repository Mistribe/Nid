using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.RealEstate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LoanEnvelope : Migration
    {
        // #180: a loan may finance an SCPI envelope instead of a property, never both.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "envelope_item_id",
                schema: "realestate",
                table: "loan",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_loan_envelope_item_id",
                schema: "realestate",
                table: "loan",
                column: "envelope_item_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loan_financed",
                schema: "realestate",
                table: "loan",
                sql: "property_item_id is null or envelope_item_id is null");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_loan_financed",
                schema: "realestate",
                table: "loan");

            migrationBuilder.DropIndex(
                name: "ix_loan_envelope_item_id",
                schema: "realestate",
                table: "loan");

            migrationBuilder.DropColumn(
                name: "envelope_item_id",
                schema: "realestate",
                table: "loan");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.RealEstate.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class LoanAsset : Migration
{
    // #188: a loan may finance an asset instead of a property or an SCPI envelope, never two of them.
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_loan_financed",
            schema: "realestate",
            table: "loan");

        migrationBuilder.AddColumn<Guid>(
            name: "asset_item_id",
            schema: "realestate",
            table: "loan",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "ix_loan_asset_item_id",
            schema: "realestate",
            table: "loan",
            column: "asset_item_id");

        migrationBuilder.AddCheckConstraint(
            name: "ck_loan_financed",
            schema: "realestate",
            table: "loan",
            sql: "num_nonnulls(property_item_id, envelope_item_id, asset_item_id) <= 1");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_loan_asset_item_id",
            schema: "realestate",
            table: "loan");

        migrationBuilder.DropCheckConstraint(
            name: "ck_loan_financed",
            schema: "realestate",
            table: "loan");

        migrationBuilder.DropColumn(
            name: "asset_item_id",
            schema: "realestate",
            table: "loan");

        migrationBuilder.AddCheckConstraint(
            name: "ck_loan_financed",
            schema: "realestate",
            table: "loan",
            sql: "property_item_id is null or envelope_item_id is null");
    }
}

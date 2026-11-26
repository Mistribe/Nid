using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Subscriptions.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class SubscriptionAssetLink : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "asset_item_id",
            schema: "subscriptions",
            table: "subscription",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "ix_subscription_asset_item_id",
            schema: "subscriptions",
            table: "subscription",
            column: "asset_item_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_subscription_asset_item_id",
            schema: "subscriptions",
            table: "subscription");

        migrationBuilder.DropColumn(
            name: "asset_item_id",
            schema: "subscriptions",
            table: "subscription");
    }
}

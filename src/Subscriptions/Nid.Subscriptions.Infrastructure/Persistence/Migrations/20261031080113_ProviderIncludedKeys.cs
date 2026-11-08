using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Subscriptions.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProviderIncludedKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "included_keys",
                schema: "subscriptions",
                table: "provider",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_provider_included",
                schema: "subscriptions",
                table: "provider",
                sql: "origin = 'catalog' or cardinality(included_keys) = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_provider_included",
                schema: "subscriptions",
                table: "provider");

            migrationBuilder.DropColumn(
                name: "included_keys",
                schema: "subscriptions",
                table: "provider");
        }
    }
}

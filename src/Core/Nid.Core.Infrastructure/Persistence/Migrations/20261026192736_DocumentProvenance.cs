using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DocumentProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "document_id",
                schema: "core",
                table: "valuation",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "document_id",
                schema: "core",
                table: "proposal",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "document_id",
                schema: "core",
                table: "flow",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "document_id",
                schema: "core",
                table: "valuation");

            migrationBuilder.DropColumn(
                name: "document_id",
                schema: "core",
                table: "proposal");

            migrationBuilder.DropColumn(
                name: "document_id",
                schema: "core",
                table: "flow");
        }
    }
}

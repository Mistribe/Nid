using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Ai.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261123143033_JobConcerned : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid[]>(
            name: "concerned",
            schema: "ai",
            table: "job",
            type: "uuid[]",
            nullable: false,
            defaultValue: new Guid[0]);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "concerned",
            schema: "ai",
            table: "job");
    }
}

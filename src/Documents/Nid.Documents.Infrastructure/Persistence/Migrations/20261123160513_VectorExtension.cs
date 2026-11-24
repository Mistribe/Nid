using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Documents.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261123160513_VectorExtension : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterDatabase()
            .Annotation("Npgsql:PostgresExtension:vector", ",,");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterDatabase()
            .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
    }
}

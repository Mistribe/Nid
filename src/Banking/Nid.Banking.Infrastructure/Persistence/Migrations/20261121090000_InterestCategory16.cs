using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InterestCategory16 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "banking",
                table: "category",
                columns: new[] { "id", "parent_id", "key", "name", "kind" },
                values: new object[] { new Guid("7e2e4790-8ac8-4ea9-b97d-cf99679feff6"), new Guid("9be6f5b7-2467-46e4-bd0d-ec14dae7137b"), "income.interest", "Intérêts", "income" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "banking",
                table: "category",
                keyColumn: "id",
                keyValue: new Guid("7e2e4790-8ac8-4ea9-b97d-cf99679feff6"));
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankFileConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_connection_source",
                schema: "banking",
                table: "connection");

            migrationBuilder.CreateIndex(
                name: "ix_connection_person_id_source",
                schema: "banking",
                table: "connection",
                columns: new[] { "person_id", "source" },
                unique: true,
                filter: "source = 'file'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_connection_source",
                schema: "banking",
                table: "connection",
                sql: "source in ('enable_banking','file')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_connection_person_id_source",
                schema: "banking",
                table: "connection");

            migrationBuilder.DropCheckConstraint(
                name: "ck_connection_source",
                schema: "banking",
                table: "connection");

            migrationBuilder.AddCheckConstraint(
                name: "ck_connection_source",
                schema: "banking",
                table: "connection",
                sql: "source in ('enable_banking')");
        }
    }
}

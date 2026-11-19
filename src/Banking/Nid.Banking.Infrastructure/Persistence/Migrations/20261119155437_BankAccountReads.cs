using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankAccountReads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "rate_limited",
                schema: "banking",
                table: "sync_run",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "account_read",
                schema: "banking",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    read_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    attended = table.Column<bool>(type: "boolean", nullable: false),
                    rate_limited = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_read", x => x.id);
                    table.ForeignKey(
                        name: "fk_account_read_account_account_id",
                        column: x => x.account_id,
                        principalSchema: "banking",
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_account_read_account_id_read_at",
                schema: "banking",
                table: "account_read",
                columns: new[] { "account_id", "read_at" });

            migrationBuilder.CreateIndex(
                name: "ix_account_read_read_at",
                schema: "banking",
                table: "account_read",
                column: "read_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_read",
                schema: "banking");

            migrationBuilder.DropColumn(
                name: "rate_limited",
                schema: "banking",
                table: "sync_run");
        }
    }
}

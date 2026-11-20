using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OperationSplits84 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_transaction_category_source",
                schema: "banking",
                table: "transaction");

            migrationBuilder.AddColumn<string>(
                name: "note",
                schema: "banking",
                table: "transaction",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "transaction_part",
                schema: "banking",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transaction_part", x => x.id);
                    table.ForeignKey(
                        name: "fk_transaction_part_category_category_id",
                        column: x => x.category_id,
                        principalSchema: "banking",
                        principalTable: "category",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transaction_part_transaction_transaction_id",
                        column: x => x.transaction_id,
                        principalSchema: "banking",
                        principalTable: "transaction",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_transaction_category_source",
                schema: "banking",
                table: "transaction",
                sql: "category_source is null or category_source in ('link','rule','hand','assistant','hint','split')");

            migrationBuilder.CreateIndex(
                name: "ix_transaction_part_category_id",
                schema: "banking",
                table: "transaction_part",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_transaction_part_transaction_id_position",
                schema: "banking",
                table: "transaction_part",
                columns: new[] { "transaction_id", "position" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "transaction_part",
                schema: "banking");

            migrationBuilder.DropCheckConstraint(
                name: "ck_transaction_category_source",
                schema: "banking",
                table: "transaction");

            migrationBuilder.DropColumn(
                name: "note",
                schema: "banking",
                table: "transaction");

            migrationBuilder.AddCheckConstraint(
                name: "ck_transaction_category_source",
                schema: "banking",
                table: "transaction",
                sql: "category_source is null or category_source in ('link','rule','hand','assistant','hint')");
        }
    }
}

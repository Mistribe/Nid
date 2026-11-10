using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropProposalTargetFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_proposal_item_target_item_id",
                schema: "core",
                table: "proposal");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "fk_proposal_item_target_item_id",
                schema: "core",
                table: "proposal",
                column: "target_item_id",
                principalSchema: "core",
                principalTable: "item",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}

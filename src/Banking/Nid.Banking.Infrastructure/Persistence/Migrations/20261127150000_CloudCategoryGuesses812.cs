using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations
{
    /// <summary>#812: a guess may come from a cloud provider's <c>decide</c> (Jev), not only from a worker here or away.</summary>
    [DbContext(typeof(BankingDbContext))]
    [Migration("20261127150000_CloudCategoryGuesses812")]
    public partial class CloudCategoryGuesses812 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "ck_category_guess_placement", schema: "banking", table: "category_guess");
            migrationBuilder.AddCheckConstraint(name: "ck_category_guess_placement", schema: "banking", table: "category_guess",
                sql: "placement in ('local','remote','cloud')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM banking.category_guess WHERE placement = 'cloud';");
            migrationBuilder.DropCheckConstraint(name: "ck_category_guess_placement", schema: "banking", table: "category_guess");
            migrationBuilder.AddCheckConstraint(name: "ck_category_guess_placement", schema: "banking", table: "category_guess",
                sql: "placement in ('local','remote')");
        }
    }
}

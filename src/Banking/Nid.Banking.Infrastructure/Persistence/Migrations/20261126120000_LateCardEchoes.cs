using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations
{
    /// <summary>A card attached after its lines were read kept their echoes as rows of their own: its stored lines go through the next pass again.</summary>
    [DbContext(typeof(BankingDbContext))]
    [Migration("20261126120000_LateCardEchoes")]
    public partial class LateCardEchoes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE banking.transaction SET reconciled_at = NULL
                WHERE link_state <> 'ignored' AND account_id IN (SELECT id FROM banking.account WHERE card_of_account_id IS NOT NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}

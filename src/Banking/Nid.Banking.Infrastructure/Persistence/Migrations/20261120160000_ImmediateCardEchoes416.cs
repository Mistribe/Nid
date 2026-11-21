using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations
{
    /// <summary>#416: the stored lines of the cards attached to an account go through the next pass again, which links their echoes.</summary>
    [DbContext(typeof(BankingDbContext))]
    [Migration("20261120160000_ImmediateCardEchoes416")]
    public partial class ImmediateCardEchoes416 : Migration
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

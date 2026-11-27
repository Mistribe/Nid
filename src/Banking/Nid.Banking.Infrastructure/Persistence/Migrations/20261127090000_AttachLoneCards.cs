using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// A stored card attached to no account goes with the only checking account its connections see, as a new one now does on connecting;
    /// its lines go through the next pass again, which pairs their echoes. The connecting person owns both, as attaching by hand asks.
    /// shortcut: receipts already on the card's lines keep their former anchor (no envelope, a narrower read) until the account's next move.
    /// </summary>
    [DbContext(typeof(BankingDbContext))]
    [Migration("20261127090000_AttachLoneCards")]
    public partial class AttachLoneCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                WITH lone AS (
                    SELECT card.account_id AS card_id, min(checking.id::text)::uuid AS parent_id
                    FROM banking.account_source card
                    JOIN banking.account c ON c.id = card.account_id AND c.kind = 'card' AND c.card_of_account_id IS NULL
                    JOIN banking.account_source seen ON seen.connection_id = card.connection_id
                    JOIN banking.account checking ON checking.id = seen.account_id AND checking.kind = 'checking'
                    GROUP BY card.account_id
                    HAVING count(DISTINCT checking.id) = 1),
                attached AS (
                    UPDATE banking.account a SET card_of_account_id = lone.parent_id FROM lone WHERE a.id = lone.card_id RETURNING a.id)
                UPDATE banking.transaction SET reconciled_at = NULL
                WHERE link_state <> 'ignored' AND account_id IN (SELECT id FROM attached);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}

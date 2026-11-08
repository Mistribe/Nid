using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Investments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialInvestments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "investments");

            migrationBuilder.CreateTable(
                name: "envelope",
                schema: "investments",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    institution = table.Column<string>(type: "text", nullable: true),
                    account_hint = table.Column<string>(type: "text", nullable: true),
                    opened_on = table.Column<LocalDate>(type: "date", nullable: true),
                    valuation_cadence = table.Column<string>(type: "text", nullable: false),
                    stale_after_days = table.Column<int>(type: "integer", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_envelope", x => x.item_id);
                    table.CheckConstraint("ck_envelope_account_hint", "account_hint is null or char_length(account_hint) between 1 and 4");
                    table.CheckConstraint("ck_envelope_cadence", "valuation_cadence in ('monthly','quarterly','yearly','manual')");
                    table.CheckConstraint("ck_envelope_kind", "kind in ('checking','savings','pea','life_insurance','per','brokerage','other')");
                    table.CheckConstraint("ck_envelope_stale_after_days", "stale_after_days is null or stale_after_days between 1 and 3650");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "envelope",
                schema: "investments");
        }
    }
}

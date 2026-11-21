using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CategorySuggestions449 : Migration
    {
        /// <summary>
        /// The stored lines' suggested pattern, as <c>CategoryRules.Suggest</c> gives it from their normalized label: past a leading
        /// « CARTE dd/mm/yy », its first two words before any holding a digit, bank prefixes stripped as <c>TransactionLabel.Normalize</c>
        /// does, kept when 2 to 60 characters long with a letter. <c>CategorySuggestionTests</c> holds it to the C#.
        /// </summary>
        public const string Backfill = """
            update banking."transaction" t set suggested_pattern = s.pattern
            from (
                select id, case when char_length(p) between 2 and 60 and p ~ '[[:alpha:]]' and strpos(normalized_label, p) > 0 then p end as pattern
                from (
                    select id, normalized_label, btrim(regexp_replace(
                        case when w1 = '' or w1 ~ '[0-9]' then '' when w2 = '' or w2 ~ '[0-9]' then w1 else w1 || ' ' || w2 end || ' ',
                        '^((PRLV SEPA|PRELEVEMENT EUROPEEN|PRELEVEMENT|VIR SEPA|VIR INST|VIREMENT|VIR|PAIEMENT PAR CARTE|PAIEMENT CB|CB) )+', '')) as p
                    from (
                        select id, normalized_label, split_part(r, ' ', 1) as w1, split_part(r, ' ', 2) as w2
                        from (select id, normalized_label, regexp_replace(normalized_label, '^CARTE [0-9]{2}/[0-9]{2}(/[0-9]{2,4})? ', '') as r
                              from banking."transaction") a
                    ) b
                ) c
            ) s
            where s.id = t.id and s.pattern is not null
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "suggested_pattern",
                schema: "banking",
                table: "transaction",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.Sql(Backfill);

            migrationBuilder.CreateTable(
                name: "suggestion_dismissal",
                schema: "banking",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pattern = table.Column<string>(type: "text", nullable: false),
                    direction = table.Column<string>(type: "text", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suggestion_dismissal", x => x.id);
                    table.CheckConstraint("ck_suggestion_dismissal_direction", "direction in ('debit','credit')");
                    table.CheckConstraint("ck_suggestion_dismissal_pattern", "char_length(pattern) between 2 and 60");
                    table.ForeignKey(
                        name: "fk_suggestion_dismissal_category_category_id",
                        column: x => x.category_id,
                        principalSchema: "banking",
                        principalTable: "category",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_transaction_account_id_suggested_pattern",
                schema: "banking",
                table: "transaction",
                columns: new[] { "account_id", "suggested_pattern" });

            migrationBuilder.CreateIndex(
                name: "ix_suggestion_dismissal_category_id",
                schema: "banking",
                table: "suggestion_dismissal",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_suggestion_dismissal_pattern_direction_category_id",
                schema: "banking",
                table: "suggestion_dismissal",
                columns: new[] { "pattern", "direction", "category_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "suggestion_dismissal",
                schema: "banking");

            migrationBuilder.DropIndex(
                name: "ix_transaction_account_id_suggested_pattern",
                schema: "banking",
                table: "transaction");

            migrationBuilder.DropColumn(
                name: "suggested_pattern",
                schema: "banking",
                table: "transaction");
        }
    }
}

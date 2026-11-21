using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GenericBankPrefixes457 : Migration
    {
        /// <summary>
        /// Recomputes every stored line's suggested pattern as <c>CategoryRules.Suggest</c> now gives it: the
        /// <see cref="CategorySuggestions449.Backfill"/> steps, with the transfer and direct debit words that open the label
        /// (« VIR DE », « PRLV ») skipped before the two words are taken. Clears a pattern that is no longer valid.
        /// <c>CategorySuggestionTests</c> holds it to the C#.
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
                        from (select id, normalized_label, regexp_replace(
                                  regexp_replace(normalized_label, '^CARTE [0-9]{2}/[0-9]{2}(/[0-9]{2,4})? ', '') || ' ',
                                  '^((VIR|VIREMENT|SEPA|INST|INSTANTANE|DE|DU|VERS|RECU|EMIS|PRLV|PRELEVEMENT|ECHEANCE) )+', '') as r
                              from banking."transaction") a
                    ) b
                ) c
            ) s
            where s.id = t.id and t.suggested_pattern is distinct from s.pattern
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Backfill);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""update banking."transaction" set suggested_pattern = null where suggested_pattern is not null""");
            migrationBuilder.Sql(CategorySuggestions449.Backfill);
        }
    }
}

using Nid.Banking.Contracts;
using Nid.Banking.Domain;

namespace Nid.Banking.Tests;

public class AccountNamesTests
{
    [Theory]
    [InlineData("Compte courant", "Compte de dépôt", "M ALICE MARTIN", "Compte courant")]
    [InlineData("immediat_debit", "Carte Visa", "M ALICE MARTIN", "Carte Visa")]
    [InlineData("DEFERRED_DEBIT", null, "MME ALICE MARTIN", "MME ALICE MARTIN")]
    [InlineData("  ", "SVGS", "M ET MME MARTIN", "M ET MME MARTIN")]
    [InlineData(null, " Livret A ", null, "Livret A")]
    [InlineData("immediat_debit", "PRIV", null, "")]
    [InlineData(null, null, null, "")]
    public void Picks_the_first_field_that_reads_as_a_name(string? details, string? product, string? holder, string expected)
        => Assert.Equal(expected, AccountNames.Pick(details, product, holder));

    [Theory]
    [InlineData("immediat_debit", true)]
    [InlineData("deferred_debit", true)]
    [InlineData("CACC", true)]
    [InlineData("orga", true)]
    [InlineData("Compte joint", false)]
    [InlineData("Livret_A de Léa", false)]
    [InlineData("PEL", false)]
    public void Tells_a_code_from_a_name(string value, bool code) => Assert.Equal(code, AccountNames.IsCode(value));

    [Theory]
    [InlineData("immediat_debit", null, CardDebit.Immediate)]
    [InlineData(null, "Carte à débit immédiat", CardDebit.Immediate)]
    [InlineData("DEFERRED_DEBIT", null, CardDebit.Deferred)]
    [InlineData("Carte Visa", "Débit différé", CardDebit.Deferred)]
    [InlineData("Carte Visa", null, null)]
    [InlineData(null, null, null)]
    public void Reads_a_cards_debit(string? details, string? product, CardDebit? expected)
        => Assert.Equal(expected, AccountNames.Debit(details, product));
}

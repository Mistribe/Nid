using Nid.Banking.Domain;

namespace Nid.Banking.Tests;

/// <summary>#450: nothing that names the household or one of its accounts leaves Banking for a model.</summary>
public class BankLabelMaskTests
{
    private static readonly IReadOnlyList<string> Names = CategorySuggestions.NameWords(["Alice Martin", "Bob Martin", "Léa Martin-Durand"]);

    [Theory]
    [InlineData("VIR SEPA ALICE MARTIN LOYER", "VIR SEPA [NOM] [NOM] LOYER")]
    [InlineData("VIR INST Lea Durand cadeau", "VIR INST [NOM] [NOM] cadeau")] // an accent or a case of its own, a dependant's
    [InlineData("PRLV SEPA BOBINAGE SARL", "PRLV SEPA BOBINAGE SARL")] // a word holding a name is another word
    [InlineData("VIR FR76 3000 6000 0112 3456 7890 189 LOYER", "VIR [IBAN] LOYER")]
    [InlineData("VIR DE FR7630006000011234567890189", "VIR DE [IBAN]")]
    [InlineData("REMB contact.client@exemple.fr", "REMB [EMAIL]")]
    [InlineData("APPEL 06 12 34 56 78 MERCI", "APPEL [TEL] MERCI")]
    [InlineData("APPEL 01.23.45.67.89", "APPEL [TEL]")]
    [InlineData("TEL +33 6 12 34 56 78", "TEL [TEL]")]
    [InlineData("TEL +44 20 7946 0958", "TEL [TEL]")]
    [InlineData("CB CARTE 4970 1234 5678 9012 PARIS", "CB CARTE [N] PARIS")]
    [InlineData("PRLV REF 123456789 ABONNEMENT", "PRLV REF [N] ABONNEMENT")]
    [InlineData("CB LIDL 0412 PARIS 12/10", "CB LIDL 0412 PARIS 12/10")] // short numbers and dates stay
    [InlineData("CB LIDL 1203 0412", "CB LIDL [N]")] // two short numbers spaced read as one long one
    public void Names_ibans_emails_phones_and_long_numbers_become_placeholders(string label, string masked)
        => Assert.Equal(masked, BankLabelMask.Mask(label, Names));

    [Fact]
    public void Without_names_only_the_shapes_are_masked()
        => Assert.Equal("VIR ALICE [N]", BankLabelMask.Mask("VIR ALICE 12345", []));
}

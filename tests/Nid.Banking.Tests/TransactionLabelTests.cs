using Nid.Banking.Domain;

namespace Nid.Banking.Tests;

public class TransactionLabelTests
{
    [Theory]
    [InlineData("PRLV SEPA NETFLIX.COM 0123", "NETFLIX.COM 0123")]
    [InlineData("PAIEMENT PAR CARTE X1234 12/10 Café Hélène", "CAFE HELENE")]
    [InlineData("VIR INST Loyer   Octobre", "LOYER OCTOBRE")]
    [InlineData("CB  AMAZON", "AMAZON")]
    [InlineData("ASSURANCE HABITATION", "ASSURANCE HABITATION")]
    [InlineData("NETFLIX CB MENSUEL", "NETFLIX CB MENSUEL")]
    [InlineData("LOYER VIR 12/10", "LOYER VIR 12/10")]
    [InlineData("CB\u00A0AMAZON", "AMAZON")]
    [InlineData("PRLV SEPA\tNETFLIX", "NETFLIX")]
    [InlineData("CARTE X9876 05/11/2026 BOULANGERIE", "BOULANGERIE")]
    public void Normalizes_the_label(string label, string expected) => Assert.Equal(expected, TransactionLabel.Normalize(label));

    [Fact]
    public void Every_listed_prefix_is_removed()
        => Assert.All(TransactionLabel.Prefixes, p => Assert.Equal("ACME", TransactionLabel.Normalize(p + "acme")));

    [Theory]
    [InlineData("NETFLIX.COM 866-579-7172 NL", "NETFLIX.COM NL")]
    [InlineData("ECHEANCE PRET 00012345 ECH 05/10/26", "ECHEANCE PRET ECH")]
    [InlineData("FREE MOBILE FM12345678", "FREE MOBILE")]
    [InlineData("12345", "")]
    public void Stem_drops_tokens_with_digits(string normalized, string expected) => Assert.Equal(expected, TransactionLabel.Stem(normalized));
}

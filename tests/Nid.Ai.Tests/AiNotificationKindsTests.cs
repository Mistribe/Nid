using Nid.Ai.Application;
using Nid.Platform.Contracts;

namespace Nid.Ai.Tests;

/// <summary>#644: Ai's published kinds, declared next to their owner, word each change of what the models may see.</summary>
public class AiNotificationKindsTests
{
    private static string Body(params (string Key, string Value)[] parameters)
    {
        var kind = new AiNotificationKinds().Kinds.Single(k => k.Kind == "ai.data_policy_changed");
        return kind.Template!.Body!(new NotificationFacts(kind.Kind, "k", null, null, parameters.ToDictionary(p => p.Key, p => p.Value)));
    }

    [Theory]
    [InlineData("chat", "L'usage « assistant » peut désormais passer par un fournisseur cloud activé.")]
    [InlineData("embed", "L'usage « recherche par le sens » peut désormais passer par un fournisseur cloud activé.")]
    [InlineData("decide", "L'usage « suggestions » peut désormais passer par un fournisseur cloud activé.")]
    [InlineData("extract", "L'usage « lecture des documents » peut désormais passer par un fournisseur cloud activé.")]
    [InlineData("other", "L'usage « other » peut désormais passer par un fournisseur cloud activé.")]
    public void A_use_is_named_in_words(string use, string body) => Assert.Equal(body, Body(("change", "cloud_on"), ("use", use)));

    [Fact]
    public void Each_change_reads_as_a_sentence()
    {
        Assert.Equal("L'usage « assistant » ne passe plus que par les workers du foyer.", Body(("change", "cloud_off"), ("use", "chat")));
        Assert.Equal("Le fournisseur cloud « mistral » est activé : il peut recevoir les demandes des usages ouverts au cloud.",
            Body(("change", "cloud_profile_on"), ("profile", "mistral")));
        Assert.Equal("Le fournisseur cloud « mistral » est désactivé.", Body(("change", "cloud_profile_off"), ("profile", "mistral")));
        Assert.Equal("« Atelier » ne reçoit plus les documents « local seulement ».", Body(("change", "safe_off"), ("worker", "Atelier")));
        Assert.Equal("Un worker peut désormais recevoir les documents « local seulement ».", Body(("change", "safe_on")));
        Assert.Equal("", Body(("change", "unknown")));
    }

    [Fact]
    public void The_document_types_sent_to_the_cloud_are_named()
    {
        Assert.Equal("Ces types de documents peuvent désormais partir vers un fournisseur cloud activé, si chaque adulte concerné l'autorise : "
            + "relevés, tableaux d'amortissement, factures, contrats, payslip.",
            Body(("change", "allowed_kinds"), ("kinds", "statement,amortization_table,invoice,contract,payslip")));
        Assert.Equal("Plus aucun document ne part vers un fournisseur cloud.", Body(("change", "allowed_kinds")));
    }
}

using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.Documents.Domain;
using Nid.TestSupport;
using static Nid.TestSupport.DocumentApi;

namespace Nid.Documents.IntegrationTests;

/// <summary>Issue #414: the expected documents, held ones and « pas disponible » left out. Today is 2025-10-15.</summary>
public class ExpectedDocumentTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Root = DocumentApi.Documents + "/expected";

    private static async Task<(string Subject, Guid Target, string Role, int Period)[]> ExpectedAsync(NidApp app, PersonId actor, string query = "")
    {
        var response = await app.RawAsync(actor, HttpMethod.Get, $"{Root}{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("items").EnumerateArray()
            .Select(e => (e.GetProperty("subject").GetString()!, e.GetProperty("targetId").GetGuid(), e.GetProperty("role").GetString()!, e.GetProperty("period").GetInt32()))
            .ToArray();
    }

    private static Task<HttpResponseMessage> DismissAsync(NidApp app, PersonId actor, string kind, Guid target, string role, int period)
        => app.RawAsync(actor, HttpMethod.Post, $"{Root}/dismissals", new { targetKind = kind, targetId = target, role, period });

    private static async Task<ItemId> LifeInsuranceAsync(NidApp app, PersonId owner)
    {
        var response = await app.RawAsync(owner, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = "Assurance vie", kind = "life_insurance", currency = "EUR", visibility = "shared",
            owners = new[] { new { personId = owner.Value, shareBp = 10_000 } },
        });
        response.EnsureSuccessStatusCode();
        return new ItemId((await JsonAsync(response)).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Lists_each_rule_and_drops_a_held_document_and_a_dismissed_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var property = await app.NewPropertyAsync("Maison", Visibility.Shared, (app.AdultA, 10_000));
        var loan = await app.NewLoanAsync("Prêt", Visibility.Shared, (app.AdultA, 10_000));
        var insurance = await LifeInsuranceAsync(app, app.AdultA);
        await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared); // a savings account expects nothing

        Assert.Equal(
            new[]
            {
                (ExpectedDocumentRules.Property, property.Value, DocumentRoles.Deed, 0),
                (ExpectedDocumentRules.Loan, loan.Value, DocumentRoles.AmortizationTable, 0),
                (ExpectedDocumentRules.LifeInsurance, insurance.Value, DocumentRoles.Statement, 2024),
                (ExpectedDocumentRules.TaxYear, app.AdultA.Value, DocumentRoles.TaxNotice, 2025),
            }.Order(),
            (await ExpectedAsync(app, app.AdultA)).Order());
        Assert.Equal([(ExpectedDocumentRules.Property, property.Value, DocumentRoles.Deed, 0)], await ExpectedAsync(app, app.AdultA, $"?itemId={property.Value}"));

        await UploadedAsync(app, app.AdultA, Pdf, $"fileName=acte.pdf&targetKind=core.item&targetId={property.Value}&role=deed");
        Assert.Empty(await ExpectedAsync(app, app.AdultA, $"?itemId={property.Value}"));

        Assert.Equal(HttpStatusCode.OK, (await DismissAsync(app, app.AdultA, TargetKinds.CoreItem, loan.Value, DocumentRoles.AmortizationTable, 0)).StatusCode);
        Assert.Empty(await ExpectedAsync(app, app.AdultA, $"?itemId={loan.Value}"));
        // Nothing left to dismiss: no longer listed.
        Assert.Equal(HttpStatusCode.NotFound, (await DismissAsync(app, app.AdultA, TargetKinds.CoreItem, loan.Value, DocumentRoles.AmortizationTable, 0)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await DismissAsync(app, app.AdultA, TargetKinds.CorePerson, app.AdultA.Value, DocumentRoles.TaxNotice, 2025)).StatusCode);
        Assert.Equal([(ExpectedDocumentRules.LifeInsurance, insurance.Value, DocumentRoles.Statement, 2024)], await ExpectedAsync(app, app.AdultA));
    }

    [Fact]
    public async Task Another_adults_item_and_tax_notice_are_neither_listed_nor_dismissed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var secret = await app.NewPropertyAsync("CoffreSecretA", Visibility.Private, (app.AdultA, 10_000));

        var raw = await app.GetRawAsync(app.AdultB, Root);
        Assert.DoesNotContain(secret.Value.ToString(), raw);
        Assert.DoesNotContain(app.AdultA.Value.ToString(), raw);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Root}?itemId={secret.Value}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DismissAsync(app, app.AdultB, TargetKinds.CoreItem, secret.Value, DocumentRoles.Deed, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DismissAsync(app, app.AdultB, TargetKinds.CorePerson, app.AdultA.Value, DocumentRoles.TaxNotice, 2025)).StatusCode);
        Assert.Contains((ExpectedDocumentRules.TaxYear, app.AdultA.Value, DocumentRoles.TaxNotice, 2025), await ExpectedAsync(app, app.AdultA));
    }
}

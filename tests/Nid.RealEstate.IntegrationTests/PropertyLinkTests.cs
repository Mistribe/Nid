using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>Spec 3C1 §3–§4: the loan → property link, set on the loan.</summary>
public class PropertyLinkTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static Dictionary<string, object?> LinkedTo(Guid property, Dictionary<string, object?> loan)
    {
        loan["propertyId"] = property;
        return loan;
    }

    [Fact]
    public async Task A_loan_is_linked_at_creation_moved_by_its_patch_and_cleared()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app));
        var flat = await NewPropertyAsync(app, Home(app, name: "Appartement"));
        var loan = await NewAsync(app, LinkedTo(home, Golden(app)));

        var detail = await DetailAsync(app, loan);
        Assert.Equal((home, "Maison"), (detail.GetProperty("propertyId").GetGuid(), S(detail, "propertyName")));
        var row = Assert.Single((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Loans))).GetProperty("items").EnumerateArray());
        Assert.Equal((home, "Maison"), (row.GetProperty("propertyId").GetGuid(), S(row, "propertyName")));

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", new { propertyId = flat })).StatusCode);
        Assert.Equal((flat, "Appartement"), ((await DetailAsync(app, loan)).GetProperty("propertyId").GetGuid(), S(await DetailAsync(app, loan), "propertyName")));

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", new { clearProperty = true })).StatusCode);
        detail = await DetailAsync(app, loan);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("propertyId").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("propertyName").ValueKind);
    }

    [Fact]
    public async Task An_unreadable_property_a_loan_an_envelope_and_an_unknown_id_all_answer_invalid_property()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await NewPropertyAsync(app, Home(app, name: "Maison de B", visibility: "private", owners: [app.AdultB]), app.AdultB);
        var otherLoan = await NewAsync(app, Golden(app, name: "Autre prêt"));
        var envelope = (await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared)).Value;
        var loan = await NewAsync(app, Golden(app));

        foreach (var target in new[] { hidden, otherLoan, envelope, Guid.NewGuid() })
        {
            Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_property", "propertyId"),
                await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Loans, LinkedTo(target, Golden(app, name: "Prêt lié")))));
            Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_property", "propertyId"),
                await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", new { propertyId = target })));
        }

        Assert.Equal(JsonValueKind.Null, (await DetailAsync(app, loan)).GetProperty("propertyId").ValueKind);
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Equal(2, await core.Items.CountAsync(i => i.Kind == "loan")); // no refused creation left a loan
    }

    [Fact]
    public async Task A_loans_reader_keeps_the_link_but_not_the_name_of_a_property_they_cannot_read()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await NewPropertyAsync(app, Home(app, name: "Maison privée", visibility: "private"));
        var loan = await NewAsync(app, LinkedTo(mine, Golden(app))); // shared, borrowed by A

        var seenByB = await DetailAsync(app, loan, app.AdultB);
        Assert.Equal(mine, seenByB.GetProperty("propertyId").GetGuid());
        Assert.Equal(JsonValueKind.Null, seenByB.GetProperty("propertyName").ValueKind);
        var rowOfB = Assert.Single((await JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Get, Loans))).GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, rowOfB.GetProperty("propertyName").ValueKind);
        Assert.Equal("Maison privée", S(await DetailAsync(app, loan), "propertyName"));
    }

    [Fact]
    public async Task A_property_the_actor_reads_but_cannot_write_takes_their_loan()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app)); // shared, owned by A: B reads it, cannot write it
        var loan = await NewAsync(app, LinkedTo(home, Golden(app, name: "Prêt de B", borrowers: [app.AdultB])), app.AdultB);

        Assert.Equal(home, (await DetailAsync(app, loan, app.AdultB)).GetProperty("propertyId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Patch, $"{Loans}/{loan}", new { clearProperty = true })).StatusCode);
    }

    [Fact]
    public async Task A_link_set_after_a_contract_was_read_turns_its_put_into_a_conflict() // Review Focus #5
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app));
        var loan = await NewAsync(app, Golden(app));
        var body = Contract("150000");
        body["version"] = await ContractVersionAsync(app, loan);

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", new { propertyId = home })).StatusCode);
        Assert.Equal((HttpStatusCode.Conflict, "realestate.loan_conflict", (string?)null),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Loans}/{loan}/contract", body)));
        Assert.Equal("200000.00", S((await DetailAsync(app, loan)).GetProperty("contract"), "principal"));
    }
}

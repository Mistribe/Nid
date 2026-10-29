using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>Rental arrears spec §5: <c>GET /properties/{id}/arrears</c> and <c>get_rent_arrears</c>. Today is 2026-10-15; a tenancy from 2026-01-01 at 800 + 50.</summary>
public class RentArrearsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private static Task<NidApp> StartAsync(PostgresFixture fixture)
        => NidApp.StartAsync(fixture, services => services.AddSingleton<IClock>(
            new FixedClock(new LocalDate(2026, 10, 15).At(new LocalTime(12, 0)).InUtc().ToInstant())));

    private static async Task<Guid> LetAsync(NidApp app, bool identity = true, string visibility = "shared", string? endsOn = null)
    {
        var body = Home(app, "Locatif", visibility, app.AdultA);
        if (identity) body["postalAddress"] = "12 rue des Lilas\n75011 Paris";
        var property = await NewPropertyAsync(app, body);
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/tenancies", new
        {
            label = "T2", startsOn = "2026-01-01", endsOn, rentMonthly = "800", chargesMonthly = "50", deposit = "800",
            tenantNames = identity ? "Mme Martin" : null, landlordName = identity ? "Alice et Bob" : null,
            landlordAddress = identity ? "1 rue A\n75001 Paris" : null,
        }));
        return property;
    }

    /// <summary>850 paid on the 5th of each month from January to <paramref name="last"/> (1 to 12).</summary>
    private static async Task PaidThroughAsync(NidApp app, Guid property, int last, int first = 1)
    {
        for (var month = first; month <= last; month++)
            await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/flows",
                new { occurredOn = $"2026-{month:00}-05", kind = "income", amount = "850", category = "rent" }));
    }

    private static async Task<JsonElement> ArrearsAsync(NidApp app, Guid property, PersonId? actor = null)
    {
        var response = await app.RawAsync(actor ?? app.AdultA, HttpMethod.Get, $"{Properties}/{property}/arrears");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static string[] Strings(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToArray();

    [Fact]
    public async Task The_months_still_due_the_totals_and_the_letter_with_the_identity()
    {
        await using var app = await StartAsync(fixture);
        var property = await LetAsync(app);
        await PaidThroughAsync(app, property, 7); // August and September are overdue, October is « en cours ».

        var view = await ArrearsAsync(app, property);

        Assert.Equal(("Locatif", "EUR", "2550.00", "1700.00"), (S(view, "propertyName"), S(view, "currency"), S(view, "total"), S(view, "overdueTotal")));
        var months = view.GetProperty("months").EnumerateArray().ToList();
        Assert.Equal(["2026-08", "2026-09", "2026-10"], months.Select(m => S(m, "month")));
        Assert.Equal([true, true, false], months.Select(m => m.GetProperty("overdue").GetBoolean()));
        Assert.Equal([true, true, false], months.Select(m => m.GetProperty("inLetter").GetBoolean()));
        Assert.Equal(("850.00", "0.00", "850.00"), (S(months[0], "due"), S(months[0], "imputed"), S(months[0], "remaining")));
        // The invariant: the months' remainders are the detail's arrears.
        Assert.Equal("2550.00", S((await PropertyAsync(app, property)).GetProperty("figures"), "arrears"));

        var letter = view.GetProperty("letter");
        Assert.Equal("1700.00", S(letter, "total"));
        Assert.Equal(["2026-08", "2026-09"], Strings(letter, "months"));
        Assert.Equal(("12 rue des Lilas\n75011 Paris", "Mme Martin", "Alice et Bob", "1 rue A\n75001 Paris"),
            (S(letter, "postalAddress"), S(letter, "tenantNames"), S(letter, "landlordName"), S(letter, "landlordAddress")));
        Assert.Empty(Strings(letter, "missing"));
        Assert.False(letter.GetProperty("leavesOutEarlierTenant").GetBoolean());
    }

    [Fact]
    public async Task Nothing_overdue_gives_no_letter_and_a_month_just_ended_is_not_overdue() // Review Focus #3
    {
        await using var app = await StartAsync(fixture);
        var property = await LetAsync(app);
        await PaidThroughAsync(app, property, 9);

        var view = await ArrearsAsync(app, property);

        Assert.Equal(("850.00", "0.00"), (S(view, "total"), S(view, "overdueTotal")));
        var month = Assert.Single(view.GetProperty("months").EnumerateArray());
        Assert.Equal(("2026-10", false, false), (S(month, "month"), month.GetProperty("overdue").GetBoolean(), month.GetProperty("inLetter").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("letter").ValueKind);

        await PaidThroughAsync(app, property, 10, first: 10); // October paid: nothing left
        Assert.Empty((await ArrearsAsync(app, property)).GetProperty("months").EnumerateArray());
    }

    /// <summary>Mme Martin (850) leaves on 16 August having paid January to June; M. Durand (950) moves in the same day.</summary>
    private static async Task<Guid> ChangeOfTenantAsync(NidApp app)
    {
        var property = await LetAsync(app, endsOn: "2026-08-16");
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/tenancies", new
        {
            label = "T2", startsOn = "2026-08-16", rentMonthly = "900", chargesMonthly = "50", tenantNames = "M. Durand",
        }));
        await PaidThroughAsync(app, property, 6);
        return property;
    }

    [Fact]
    public async Task A_change_of_tenant_writes_to_the_new_tenant_for_his_months_only() // Review Focus #2
    {
        await using var app = await StartAsync(fixture);
        // Mme Martin owes July and her August days; M. Durand owes his August days and September.
        var property = await ChangeOfTenantAsync(app);

        var view = await ArrearsAsync(app, property);

        // August is two rows, one per tenant, each of its own days: 850 × 15 ÷ 31 and 950 × 16 ÷ 31.
        var months = view.GetProperty("months").EnumerateArray().ToList();
        Assert.Equal([("2026-07", "850.00", false), ("2026-08", "411.29", false), ("2026-08", "490.32", true), ("2026-09", "950.00", true), ("2026-10", "950.00", false)],
            months.Select(m => (S(m, "month"), S(m, "remaining"), m.GetProperty("inLetter").GetBoolean())));
        Assert.Equal([true, true, true, true, false], months.Select(m => m.GetProperty("overdue").GetBoolean()));
        var letter = view.GetProperty("letter");
        Assert.Equal(["2026-08", "2026-09"], Strings(letter, "months"));
        Assert.Equal("1440.32", S(letter, "total"));
        Assert.True(letter.GetProperty("leavesOutEarlierTenant").GetBoolean());
        Assert.False(letter.GetProperty("formerTenant").GetBoolean());
        Assert.Equal("M. Durand", S(letter, "tenantNames"));
        Assert.Equal(["landlord_name", "landlord_address"], Strings(letter, "missing"));
    }

    [Fact]
    public async Task A_new_tenant_who_pays_on_time_never_settles_the_former_one_s_debt()
    {
        await using var app = await StartAsync(fixture);
        var property = await ChangeOfTenantAsync(app);
        foreach (var (on, amount) in new[] { ("2026-08-16", "490.32"), ("2026-09-05", "950"), ("2026-10-05", "950") })
            await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/flows",
                new { occurredOn = on, kind = "income", amount, category = "rent" }));

        var view = await ArrearsAsync(app, property);

        // Only Mme Martin's July and August days remain: the letter is hers, and the figure is her remainder only.
        Assert.Equal([("2026-07", "850.00"), ("2026-08", "411.29")], view.GetProperty("months").EnumerateArray().Select(m => (S(m, "month"), S(m, "remaining"))));
        var letter = view.GetProperty("letter");
        Assert.Equal(("1261.29", "Mme Martin"), (S(letter, "total"), S(letter, "tenantNames")));
        Assert.True(letter.GetProperty("formerTenant").GetBoolean());
        Assert.False(letter.GetProperty("leavesOutEarlierTenant").GetBoolean());
        Assert.Equal("1261.29", S((await PropertyAsync(app, property)).GetProperty("figures"), "arrears"));

        // M. Durand's months are quittances, read from his own account.
        foreach (var month in new[] { "2026-09", "2026-10" })
        {
            var receipt = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Properties}/{property}/receipts?month={month}");
            Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
            Assert.Equal("quittance", S(await JsonAsync(receipt), "kind"));
        }
    }

    [Fact]
    public async Task Missing_identity_is_listed_and_nothing_is_invented()
    {
        await using var app = await StartAsync(fixture);
        var property = await LetAsync(app, identity: false);
        await PaidThroughAsync(app, property, 7);

        var letter = (await ArrearsAsync(app, property)).GetProperty("letter");

        Assert.Equal(["postal_address", "tenant_names", "landlord_name", "landlord_address"], Strings(letter, "missing"));
        foreach (var name in new[] { "postalAddress", "tenantNames", "landlordName", "landlordAddress" })
            Assert.Equal(JsonValueKind.Null, letter.GetProperty(name).ValueKind);
    }

    [Fact]
    public async Task The_other_adult_s_private_property_is_a_404_and_a_reader_of_a_shared_one_gets_the_view()
    {
        await using var app = await StartAsync(fixture);
        var hidden = await LetAsync(app, visibility: "private");
        var shared = await LetAsync(app);
        Assert.False((await PropertyAsync(app, shared, app.AdultB)).GetProperty("canWrite").GetBoolean());

        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Properties}/{hidden}/arrears")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Properties}/{Guid.NewGuid()}/arrears")).StatusCode);
        Assert.Equal("Locatif", S(await ArrearsAsync(app, shared, app.AdultB), "propertyName"));
    }

    [Fact]
    public async Task The_assistant_lists_the_arrears_without_any_identity_and_never_the_other_adult_s_private_property() // Review Focus #4
    {
        await using var app = await StartAsync(fixture);
        var hidden = await LetAsync(app, visibility: "private");
        var shared = await LetAsync(app);
        var paidUp = await LetAsync(app);
        await PaidThroughAsync(app, hidden, 7);
        await PaidThroughAsync(app, shared, 7);
        await PaidThroughAsync(app, paidUp, 10);

        async Task<(JsonElement Root, string Text)> ListAsync(PersonId who, string scope)
        {
            await using var client = await McpHarness.ClientAsync(app, (await TokenApi.CreateAsync(app, who, TokenPresets.Read)).Secret);
            var result = await client.CallToolAsync("get_rent_arrears", new Dictionary<string, object?> { ["scope"] = scope });
            Assert.True(result.IsError != true, McpHarness.TextOf(result));
            var text = McpHarness.TextOf(result);
            return (JsonDocument.Parse(text).RootElement.Clone(), text);
        }

        var (owner, ownerText) = await ListAsync(app.AdultA, "me");
        Assert.Equal(new[] { hidden, shared }.Order(),owner.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("property").GetGuid()).Order());
        var item = owner.GetProperty("items").EnumerateArray().First(i => i.GetProperty("property").GetGuid() == shared);
        Assert.Equal(("Locatif", "EUR", "2550.00", "1700.00"), (S(item, "name"), S(item, "currency"), S(item, "total"), S(item, "overdueTotal")));
        Assert.Equal(3, item.GetProperty("months").GetArrayLength());
        foreach (var identity in new[] { "Martin", "Lilas", "Alice", "rue A" })
            Assert.DoesNotContain(identity, ownerText);

        var (other, _) = await ListAsync(app.AdultB, "household");
        Assert.Equal(new[] { shared }, other.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("property").GetGuid()));
    }
}

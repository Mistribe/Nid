using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Goals.Application;
using Nid.Goals.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.IntegrationTests.Admin;
using Nid.Subscriptions.IntegrationTests.Mcp;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Goals;

/// <summary>Savings goals spec §3, §5, D1, D5, D6, D10, D13, D14: the goal reads. Today is <see cref="NidApp.GoldenCaseToday"/> (2025-10-15).</summary>
public class GoalReadTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Goals = "/api/v1/goals";
    private static readonly LocalDate Today = NidApp.GoldenCaseToday;

    private static string Iso(LocalDate date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<ItemId> EnvelopeAsync(NidApp app, PersonId owner, Visibility visibility, string? value, string currency = "EUR",
        string? contributions = null, string name = "Livret")
    {
        var id = await app.NewEnvelopeAsync(name, owner, visibility, currency);
        if (contributions is not null)
            (await app.RawAsync(owner, HttpMethod.Post, $"/api/v1/investments/envelopes/{id.Value}/flows",
                new { occurredOn = Iso(Today.PlusDays(-100)), kind = "contribution", amount = new { amount = contributions, currency } })).EnsureSuccessStatusCode();
        if (value is not null)
            (await app.RawAsync(owner, HttpMethod.Post, $"/api/v1/investments/envelopes/{id.Value}/valuations",
                new { asOf = Iso(Today.PlusDays(-5)), amount = new { amount = value, currency } })).EnsureSuccessStatusCode();
        return id;
    }

    private static async Task<ItemId> GoalAsync(
        NidApp app, PersonId actor, string name = "Voyage", string kind = GoalKinds.Savings, string? targetAmount = "10000", int? months = null,
        LocalDate? targetDate = null, Visibility visibility = Visibility.Shared, params (ItemId Envelope, int ShareBp)[] funding)
    {
        var response = await app.RawAsync(actor, HttpMethod.Post, Goals, new
        {
            name, kind, targetAmount, months, targetDate = targetDate is { } d ? Iso(d) : null,
            visibility = visibility == Visibility.Private ? "private" : "shared",
            owners = new[] { new { personId = actor.Value, shareBp = 10_000 } },
            funding = funding.Select(f => new { envelopeId = f.Envelope.Value, shareBp = f.ShareBp }).ToArray(),
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return new ItemId(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid());
    }

    private static async Task<JsonElement> GetAsync(NidApp app, PersonId actor, string url)
    {
        var response = await app.RawAsync(actor, HttpMethod.Get, url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static Task<JsonElement> DetailAsync(NidApp app, PersonId actor, ItemId goal, string scope = "me")
        => GetAsync(app, actor, $"{Goals}/{goal.Value}?scope={scope}");

    private static decimal? Amount(JsonElement view, string property)
        => view.GetProperty(property).ValueKind == JsonValueKind.Null
            ? null
            : decimal.Parse(view.GetProperty(property).GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);

    private static string[] Flags(JsonElement view) => view.GetProperty("flags").EnumerateArray().Select(f => f.GetString()!).ToArray();

    private static JsonElement Line(JsonElement detail, ItemId envelope)
        => detail.GetProperty("funding").EnumerateArray().Single(l => l.GetProperty("envelopeId").GetGuid() == envelope.Value);

    private static async Task WriteRateAsync(NidApp app, decimal eurPerUsd)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IFxRateStore>()
            .WriteAsync([new FxRate(Today, Currency.Eur, eurPerUsd, FxRateSource.Ecb)], default);
    }

    [Fact]
    public async Task Savings_goal_with_one_envelope_on_track()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await EnvelopeAsync(app, app.AdultA, Visibility.Shared, "4000", contributions: "12000");
        var goal = await GoalAsync(app, app.AdultA, targetDate: Today.PlusMonths(8), funding: (envelope, 10_000));

        var view = (await DetailAsync(app, app.AdultA, goal)).GetProperty("goal");

        Assert.Equal((10_000m, 4000m, 6000m, 1000m, 750m), (Amount(view, "target"), Amount(view, "reached"), Amount(view, "remaining"),
            Amount(view, "monthlyPace"), Amount(view, "monthlyNeeded")));
        Assert.Equal(40m, view.GetProperty("progressPct").GetDecimal());
        Assert.Equal(Iso(Today.PlusMonths(6)), view.GetProperty("projectedOn").GetString());
        Assert.Equal(GoalStatuses.OnTrack, view.GetProperty("status").GetString());
        Assert.Equal("EUR", view.GetProperty("target").GetProperty("currency").GetString());
        Assert.False(view.GetProperty("partial").GetBoolean());
        Assert.Empty(Flags(view));
    }

    [Fact]
    public async Task Half_share_counts_half()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await EnvelopeAsync(app, app.AdultA, Visibility.Shared, "4000", contributions: "12000");
        var goal = await GoalAsync(app, app.AdultA, funding: (envelope, 5000));

        var detail = await DetailAsync(app, app.AdultA, goal);

        Assert.Equal((2000m, 500m), (Amount(detail.GetProperty("goal"), "reached"), Amount(detail.GetProperty("goal"), "monthlyPace")));
        var line = Line(detail, envelope);
        Assert.Equal((5000, 4000m, 2000m), (line.GetProperty("shareBp").GetInt32(), Amount(line, "value"), Amount(line, "contribution")));
    }

    [Fact]
    public async Task Private_envelope_of_other_adult_reads_partial()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await EnvelopeAsync(app, app.AdultA, Visibility.Shared, "1000", name: "Livret commun");
        var theirs = await EnvelopeAsync(app, app.AdultB, Visibility.Private, "4000", name: "ZZSECRETB");
        var goal = await GoalAsync(app, app.AdultB, funding: [(mine, 10_000), (theirs, 10_000)]);

        var raw = await app.GetRawAsync(app.AdultA, $"{Goals}/{goal.Value}?scope=me");
        var detail = JsonDocument.Parse(raw).RootElement;

        Assert.Equal(1000m, Amount(detail.GetProperty("goal"), "reached"));
        Assert.True(detail.GetProperty("goal").GetProperty("partial").GetBoolean());
        Assert.Contains(GoalFlags.Partial, Flags(detail.GetProperty("goal")));
        Assert.Equal([mine.Value], detail.GetProperty("funding").EnumerateArray().Select(l => l.GetProperty("envelopeId").GetGuid()));
        Assert.DoesNotContain(theirs.Value.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ZZSECRETB", raw, StringComparison.Ordinal);
        // B sees all of it.
        Assert.Equal(5000m, Amount((await DetailAsync(app, app.AdultB, goal)).GetProperty("goal"), "reached"));
    }

    [Fact]
    public async Task Household_scope_hides_own_private_envelope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await EnvelopeAsync(app, app.AdultA, Visibility.Private, "3000");
        var goal = await GoalAsync(app, app.AdultA, funding: (mine, 10_000));

        var household = await DetailAsync(app, app.AdultA, goal, "household");
        var me = await DetailAsync(app, app.AdultA, goal, "me");

        Assert.Equal((0m, true), (Amount(household.GetProperty("goal"), "reached")!.Value, household.GetProperty("goal").GetProperty("partial").GetBoolean()));
        Assert.Empty(household.GetProperty("funding").EnumerateArray());
        Assert.Equal((3000m, false), (Amount(me.GetProperty("goal"), "reached")!.Value, me.GetProperty("goal").GetProperty("partial").GetBoolean()));
    }

    [Fact]
    public async Task Foreign_envelope_without_rate_is_left_out()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var euros = await EnvelopeAsync(app, app.AdultA, Visibility.Shared, "500");
        var dollars = await EnvelopeAsync(app, app.AdultA, Visibility.Shared, "1000", "USD", name: "Brokerage");
        var goal = await GoalAsync(app, app.AdultA, funding: [(euros, 10_000), (dollars, 10_000)]);

        var detail = await DetailAsync(app, app.AdultA, goal);

        Assert.Equal(500m, Amount(detail.GetProperty("goal"), "reached"));
        Assert.Contains(GoalFlags.MissingCurrency, Flags(detail.GetProperty("goal")));
        var line = Line(detail, dollars);
        Assert.Contains(GoalFlags.MissingCurrency, Flags(line));
        Assert.Null(Amount(line, "contribution"));
        Assert.DoesNotContain(GoalFlags.MissingCurrency, Flags(Line(detail, euros)));
    }

    [Fact]
    public async Task Foreign_envelope_with_rate_is_converted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await WriteRateAsync(app, 0.9m); // 1 USD = 0.90 EUR
        var dollars = await EnvelopeAsync(app, app.AdultA, Visibility.Shared, "1000", "USD", contributions: "1200", name: "Brokerage");
        var goal = await GoalAsync(app, app.AdultA, funding: (dollars, 10_000));

        var detail = await DetailAsync(app, app.AdultA, goal);

        Assert.Equal((900m, 90m), (Amount(detail.GetProperty("goal"), "reached"), Amount(detail.GetProperty("goal"), "monthlyPace")));
        Assert.Empty(Flags(detail.GetProperty("goal")));
        var line = Line(detail, dollars);
        Assert.Equal(("USD", "EUR"), (line.GetProperty("value").GetProperty("currency").GetString(), line.GetProperty("contribution").GetProperty("currency").GetString()));
        Assert.Equal((1000m, 900m), (Amount(line, "value"), Amount(line, "contribution")));
    }

    [Fact]
    public async Task Closed_envelope_counts_zero()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await EnvelopeAsync(app, app.AdultA, Visibility.Shared, "1000");
        var goal = await GoalAsync(app, app.AdultA, funding: (envelope, 10_000));
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{envelope.Value}/close",
            new { closedOn = Iso(Today.PlusDays(-1)) })).EnsureSuccessStatusCode();

        var detail = await DetailAsync(app, app.AdultA, goal);

        Assert.Equal(0m, Amount(detail.GetProperty("goal"), "reached"));
        Assert.Contains(GoalFlags.ClosedEnvelope, Flags(detail.GetProperty("goal")));
        Assert.Contains(GoalFlags.ClosedEnvelope, Flags(Line(detail, envelope)));
        Assert.Equal(0m, Amount(Line(detail, envelope), "contribution"));
    }

    [Fact]
    public async Task Over_allocated_when_reader_sees_more_than_hundred_percent()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await EnvelopeAsync(app, app.AdultA, Visibility.Shared, "1000");
        var hers = await GoalAsync(app, app.AdultA, "Privé", visibility: Visibility.Private, funding: (envelope, 6000));
        var his = await GoalAsync(app, app.AdultB, "Commun", funding: (envelope, 6000)); // B does not see A's goal (D10)

        Assert.Contains(GoalFlags.OverAllocated, Flags(Line(await DetailAsync(app, app.AdultA, hers), envelope)));
        Assert.Contains(GoalFlags.OverAllocated, Flags(Line(await DetailAsync(app, app.AdultA, his), envelope)));
        Assert.DoesNotContain(GoalFlags.OverAllocated, Flags(Line(await DetailAsync(app, app.AdultB, his), envelope)));
        Assert.DoesNotContain(GoalFlags.OverAllocated, Flags(Line(await DetailAsync(app, app.AdultA, his, "household"), envelope)));
    }

    /// <summary>D10: a goal the reader reads counts on its envelopes even when the scope leaves it out (her private goal, scope household).</summary>
    [Fact]
    public async Task Own_private_goal_counts_in_household_scope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await EnvelopeAsync(app, app.AdultA, Visibility.Shared, "1000");
        var hers = await GoalAsync(app, app.AdultA, "Privé", visibility: Visibility.Private, funding: (envelope, 6000));
        await GoalAsync(app, app.AdultB, "Commun", funding: (envelope, 5000));

        Assert.Contains(GoalFlags.OverAllocated, Flags(Line(await DetailAsync(app, app.AdultA, hers, "household"), envelope)));
    }

    [Fact]
    public async Task Envelope_without_value_reads_no_value()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var empty = await EnvelopeAsync(app, app.AdultA, Visibility.Shared, null);
        var goal = await GoalAsync(app, app.AdultA, funding: (empty, 10_000));

        var line = Line(await DetailAsync(app, app.AdultA, goal), empty);

        Assert.Contains(GoalFlags.NoValue, Flags(line));
        Assert.Equal(JsonValueKind.Null, line.GetProperty("value").ValueKind);
        Assert.Equal(0m, Amount(line, "contribution"));
    }

    /// <summary>D5, D6: a source without a rate has no monthly amount and names the currency; the target comes from the others.</summary>
    [Fact]
    public async Task Emergency_fund_leaves_out_a_source_without_rate()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewMonthlyAsync(app.AdultA, 50m);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/realestate/loans", new
        {
            name = "Prêt US", currency = "USD", visibility = "shared",
            borrowers = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            principal = "10000", disbursedOn = "2025-01-01", firstPaymentDate = "2025-01-15", totalPayments = 24,
            rounding = "half_up", firstPeriod = "full",
            phases = new[] { new { seq = 1, mode = "annuity", annualRate = "0.01", rateType = "fixed" } },
        })).EnsureSuccessStatusCode();
        var goal = await GoalAsync(app, app.AdultA, "Précaution", GoalKinds.EmergencyFund, targetAmount: null, months: 6);

        var detail = await DetailAsync(app, app.AdultA, goal);

        Assert.Equal(300m, Amount(detail.GetProperty("goal"), "target"));
        Assert.Contains(GoalFlags.MissingCurrency, Flags(detail.GetProperty("goal")));
        var sources = detail.GetProperty("commitments").EnumerateArray().ToDictionary(c => c.GetProperty("source").GetString()!);
        Assert.Equal((JsonValueKind.Null, "USD"), (sources["loans"].GetProperty("monthly").ValueKind, sources["loans"].GetProperty("missingCurrency").GetString()));
        Assert.Equal((50m, JsonValueKind.Null), (Amount(sources["subscriptions"], "monthly"), sources["subscriptions"].GetProperty("missingCurrency").ValueKind));
    }

    [Fact]
    public async Task Free_goal_uses_entries()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await GoalAsync(app, app.AdultA);
        foreach (var (on, amount) in new[] { (Today.PlusDays(-183), "1000"), (Today, "3000") })
            (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Goals}/{goal.Value}/progress/{Iso(on)}", new { amount })).EnsureSuccessStatusCode();

        var detail = await DetailAsync(app, app.AdultA, goal);

        Assert.Equal(3000m, Amount(detail.GetProperty("goal"), "reached"));
        Assert.Equal(Math.Round(2000m / (183m / 30.4375m), 2, MidpointRounding.AwayFromZero), Amount(detail.GetProperty("goal"), "monthlyPace"));
        Assert.Equal([Iso(Today.PlusDays(-183)), Iso(Today)], detail.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("on").GetString()));
        Assert.Empty(detail.GetProperty("funding").EnumerateArray());
    }

    [Fact]
    public async Task Emergency_fund_follows_commitments()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewMonthlyAsync(app.AdultA, 50m);
        var goal = await GoalAsync(app, app.AdultA, "Précaution", GoalKinds.EmergencyFund, targetAmount: null, months: 6);

        Assert.Equal(300m, Amount((await DetailAsync(app, app.AdultA, goal)).GetProperty("goal"), "target"));

        await app.NewMonthlyAsync(app.AdultA, 25m);
        var detail = await DetailAsync(app, app.AdultA, goal);

        Assert.Equal(450m, Amount(detail.GetProperty("goal"), "target"));
        Assert.Equal(6, detail.GetProperty("months").GetInt32());
        var source = Assert.Single(detail.GetProperty("commitments").EnumerateArray());
        Assert.Equal(("subscriptions", 2, 75m), (source.GetProperty("source").GetString(), source.GetProperty("count").GetInt32(), Amount(source, "monthly")));
    }

    [Fact]
    public async Task Emergency_fund_without_commitments_has_no_target()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await GoalAsync(app, app.AdultA, "Précaution", GoalKinds.EmergencyFund, targetAmount: null);

        var view = (await DetailAsync(app, app.AdultA, goal)).GetProperty("goal");

        Assert.Equal(JsonValueKind.Null, view.GetProperty("target").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("reached").ValueKind);
        Assert.Contains(GoalFlags.NoCommitments, Flags(view));
    }

    [Fact]
    public async Task List_hides_closed_unless_asked()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var open = await GoalAsync(app, app.AdultA, "Ouvert");
        var closed = await GoalAsync(app, app.AdultA, "Clos");
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Goals}/{closed.Value}/close")).EnsureSuccessStatusCode();

        var ids = (await GetAsync(app, app.AdultA, $"{Goals}?scope=me")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid());
        var all = (await GetAsync(app, app.AdultA, $"{Goals}?scope=me&closed=true")).GetProperty("items").EnumerateArray().ToList();

        Assert.Equal([open.Value], ids);
        Assert.Equal(new[] { open.Value, closed.Value }.Order(), all.Select(i => i.GetProperty("id").GetGuid()).Order());
        Assert.True(all.Single(i => i.GetProperty("id").GetGuid() == closed.Value).GetProperty("closed").GetBoolean());
    }

    [Fact]
    public async Task List_is_ordered_late_first()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var reached = await GoalAsync(app, app.AdultA, "Omega", targetDate: Today.PlusMonths(2));
        (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Goals}/{reached.Value}/progress/{Iso(Today)}", new { amount = "10000" })).EnsureSuccessStatusCode();
        await GoalAsync(app, app.AdultA, "Beta");
        await GoalAsync(app, app.AdultA, "Alpha", targetDate: Today.PlusMonths(8));
        await GoalAsync(app, app.AdultA, "Delta", targetDate: Today.PlusMonths(4));
        var late = await GoalAsync(app, app.AdultA, "Zeta", targetDate: Today.PlusMonths(1));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            var stored = (await store.FindAsync(late, default))!;
            Assert.True((await store.UpdateAsync(stored with { TargetDate = Today.PlusDays(-1) }, default)).IsSuccess); // past due: no route sets a past date
        }

        var items = (await GetAsync(app, app.AdultA, $"{Goals}?scope=me")).GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(["Zeta", "Delta", "Alpha", "Omega", "Beta"], items.Select(i => i.GetProperty("name").GetString()));
        Assert.Equal([GoalStatuses.PastDue, GoalStatuses.Behind, GoalStatuses.Behind, GoalStatuses.Reached, GoalStatuses.NoDate],
            items.Select(i => i.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Mcp_get_goals_matches_the_route()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await EnvelopeAsync(app, app.AdultA, Visibility.Shared, "1000");
        var goal = await GoalAsync(app, app.AdultA, funding: (envelope, 10_000));
        var token = await TokenRouteTests.CreateAsync(app, app.AdultA, TokenPresets.Read);
        await using var client = await McpHarness.ClientAsync(app, token.Secret);

        async Task<string> CallAsync(string tool, Dictionary<string, object?> args)
        {
            var result = await client.CallToolAsync(tool, args);
            Assert.True(result.IsError != true, McpHarness.TextOf(result));
            return McpHarness.TextOf(result);
        }

        Assert.Equal(JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{Goals}?scope=me&closed=true")).RootElement.ToString(),
            JsonDocument.Parse(await CallAsync("get_goals", new() { ["scope"] = "me", ["closed"] = true })).RootElement.ToString());
        Assert.Equal(JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{Goals}/{goal.Value}")).RootElement.ToString(),
            JsonDocument.Parse(await CallAsync("get_goal", new() { ["id"] = goal.Value })).RootElement.ToString());
        Assert.Contains(goal.Value.ToString(), await CallAsync("get_goals", new()));
    }
}

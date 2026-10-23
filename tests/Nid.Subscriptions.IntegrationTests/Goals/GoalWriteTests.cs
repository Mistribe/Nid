using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Goals.Application;
using Nid.Goals.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Goals;

/// <summary>Savings goals spec §2, D10, D10c, D12: the goal writes. Today is <see cref="NidApp.GoldenCaseToday"/> (2025-10-15).</summary>
public class GoalWriteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Goals = "/api/v1/goals";

    private static Dictionary<string, object?> Body(
        NidApp app, string name = "Voyage", string kind = GoalKinds.Savings, string? targetAmount = "10000", int? months = null,
        string? targetDate = null, string visibility = "shared", params (ItemId Envelope, int ShareBp)[] funding) => new()
    {
        ["name"] = name, ["kind"] = kind, ["targetAmount"] = targetAmount, ["months"] = months, ["targetDate"] = targetDate,
        ["note"] = "Japon", ["visibility"] = visibility,
        ["owners"] = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        ["funding"] = funding.Select(f => new { envelopeId = f.Envelope.Value, shareBp = f.ShareBp }).ToArray(),
    };

    private static Dictionary<string, object?> Edit(string name = "Voyage", string? targetAmount = "10000", string? targetDate = null, params (ItemId Envelope, int ShareBp)[] funding)
        => new()
        {
            ["name"] = name, ["targetAmount"] = targetAmount, ["targetDate"] = targetDate, ["note"] = "Japon",
            ["funding"] = funding.Select(f => new { envelopeId = f.Envelope.Value, shareBp = f.ShareBp }).ToArray(),
        };

    /// <summary>A <c>PUT</c> of <paramref name="edit"/> with the goal's current version, unless the edit names one.</summary>
    private static async Task<HttpResponseMessage> EditAsync(NidApp app, PersonId actor, ItemId goal, Dictionary<string, object?> edit)
    {
        if (!edit.ContainsKey("version"))
            edit["version"] = (await StoredAsync(app, (store, _) => store.FindAsync(goal, default)))!.Version;
        return await app.RawAsync(actor, HttpMethod.Put, $"{Goals}/{goal.Value}", edit);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<ItemId> CreatedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return new ItemId((await JsonAsync(response)).GetProperty("id").GetGuid());
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    private static async Task<T> StoredAsync<T>(NidApp app, Func<IGoalStore, IItemRegistry, Task<T>> read)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<IGoalStore>(), scope.ServiceProvider.GetRequiredService<IItemRegistry>());
    }

    private static Task<IReadOnlyList<Funding>> FundingAsync(NidApp app, ItemId goal)
        => StoredAsync(app, (store, _) => store.FundingOfAsync([goal], default));

    private static Task<HttpResponseMessage> PostAsync(NidApp app, PersonId actor, string url, object? body = null)
        => app.RawAsync(actor, HttpMethod.Post, url, body);

    [Fact]
    public async Task Creates_a_savings_goal()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);

        var id = await CreatedAsync(await PostAsync(app, app.AdultA, Goals, Body(app, targetDate: "2026-06-30", funding: (envelope, 5000))));

        var (goal, item) = await StoredAsync(app, async (store, items) => ((await store.FindAsync(id, default))!, (await items.FindAsync(id, default))!));
        Assert.Equal((GoalKinds.Savings, 10_000m, (int?)null, new LocalDate(2026, 6, 30), "Japon"),
            (goal.Kind, goal.TargetAmount!.Value, goal.Months, goal.TargetDate!.Value, goal.Note));
        Assert.Equal(("Voyage", Visibility.Shared), (item.DisplayName, item.Visibility));
        Assert.Equal(new Funding(id, envelope, 5000), Assert.Single(await FundingAsync(app, id)));
    }

    [Fact]
    public async Task Creates_an_emergency_fund_with_six_months_by_default()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var id = await CreatedAsync(await PostAsync(app, app.AdultA, Goals, Body(app, "Précaution", GoalKinds.EmergencyFund, targetAmount: null)));

        var goal = (await StoredAsync(app, (store, _) => store.FindAsync(id, default)))!;
        Assert.Equal((GoalKinds.EmergencyFund, 6, (decimal?)null), (goal.Kind, goal.Months!.Value, goal.TargetAmount));
    }

    [Fact]
    public async Task Refuses_unknown_kind()
    {
        await using var app = await NidApp.StartAsync(fixture);

        await AssertRefusedAsync(await PostAsync(app, app.AdultA, Goals, Body(app, kind: "car")), HttpStatusCode.UnprocessableEntity, GoalsCodes.UnknownKind);
    }

    [Fact]
    public async Task Refuses_savings_without_target()
    {
        await using var app = await NidApp.StartAsync(fixture);

        await AssertRefusedAsync(await PostAsync(app, app.AdultA, Goals, Body(app, targetAmount: null)), HttpStatusCode.UnprocessableEntity, GoalsCodes.InvalidTarget);
        await AssertRefusedAsync(await PostAsync(app, app.AdultA, Goals, Body(app, targetAmount: "0")), HttpStatusCode.UnprocessableEntity, GoalsCodes.InvalidTarget);
        await AssertRefusedAsync(await PostAsync(app, app.AdultA, Goals, Body(app, months: 6)), HttpStatusCode.UnprocessableEntity, GoalsCodes.InvalidTarget);
    }

    /// <summary>numeric(19,4): an amount too large or too precise for the column is a located 422, never a 500.</summary>
    [Theory]
    [InlineData("12345678901234567")]
    [InlineData("10.123456")]
    public async Task Refuses_an_amount_the_column_cannot_hold(string amount)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA);

        var created = await PostAsync(app, app.AdultA, Goals, Body(app, targetAmount: amount));
        var edited = await EditAsync(app, app.AdultA, goal, Edit(targetAmount: amount));
        var progress = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Goals}/{goal.Value}/progress/2025-10-01", new { amount });

        foreach (var (response, field) in new[] { (created, "targetAmount"), (edited, "targetAmount"), (progress, "amount") })
        {
            await AssertRefusedAsync(response, HttpStatusCode.UnprocessableEntity, GoalsCodes.InvalidTarget);
            Assert.Equal(field, (await JsonAsync(response)).GetProperty("errors")[0].GetProperty("path").GetString());
        }
    }

    [Fact]
    public async Task Refuses_months_out_of_range()
    {
        await using var app = await NidApp.StartAsync(fixture);

        foreach (var months in new[] { 0, 25 })
            await AssertRefusedAsync(await PostAsync(app, app.AdultA, Goals, Body(app, kind: GoalKinds.EmergencyFund, targetAmount: null, months: months)),
                HttpStatusCode.UnprocessableEntity, GoalsCodes.InvalidMonths);
        await AssertRefusedAsync(await PostAsync(app, app.AdultA, Goals, Body(app, kind: GoalKinds.EmergencyFund, months: 6)),
            HttpStatusCode.UnprocessableEntity, GoalsCodes.InvalidTarget);
    }

    [Fact]
    public async Task Refuses_target_date_in_the_past()
    {
        await using var app = await NidApp.StartAsync(fixture);

        await AssertRefusedAsync(await PostAsync(app, app.AdultA, Goals, Body(app, targetDate: "2025-10-14")),
            HttpStatusCode.UnprocessableEntity, GoalsCodes.TargetDatePast);
        await CreatedAsync(await PostAsync(app, app.AdultA, Goals, Body(app, targetDate: "2025-10-15")));
    }

    [Fact]
    public async Task Refuses_share_out_of_range()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var other = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);

        foreach (var funding in new[] { new[] { (envelope, 0) }, [(envelope, 10_001)], [(envelope, 5000), (other, 1000), (envelope, 1000)] })
            await AssertRefusedAsync(await PostAsync(app, app.AdultA, Goals, Body(app, funding: funding)),
                HttpStatusCode.UnprocessableEntity, GoalsCodes.InvalidShare);
    }

    [Fact]
    public async Task Refuses_envelope_of_another_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await app.NewEnvelopeAsync("PEA de B", app.AdultB, Visibility.Private);

        var refused = await PostAsync(app, app.AdultA, Goals, Body(app, funding: (hidden, 5000)));
        var unknown = await PostAsync(app, app.AdultA, Goals, Body(app, funding: (new ItemId(Guid.NewGuid()), 5000)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await refused.Content.ReadAsStringAsync();
        Assert.Equal(GoalsCodes.EnvelopeNotVisible, JsonDocument.Parse(problem).RootElement.GetProperty("code").GetString());
        // As an envelope that does not exist: nothing tells B's envelope apart.
        Assert.Equal(problem, await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Accepts_sixty_plus_forty()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);

        await CreatedAsync(await PostAsync(app, app.AdultA, Goals, Body(app, "Voyage", funding: (envelope, 6000))));
        await CreatedAsync(await PostAsync(app, app.AdultA, Goals, Body(app, "Voiture", funding: (envelope, 4000))));
    }

    [Fact]
    public async Task Refuses_sixty_plus_fifty_with_free_share()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        await CreatedAsync(await PostAsync(app, app.AdultA, Goals, Body(app, "Voyage", funding: (envelope, 6000))));

        var refused = await PostAsync(app, app.AdultA, Goals, Body(app, "Voiture", funding: (envelope, 5000)));

        await AssertRefusedAsync(refused, HttpStatusCode.Conflict, GoalsCodes.ShareExceeded);
        Assert.Equal(4000, (await JsonAsync(refused)).GetProperty("freeBp").GetInt32());
    }

    [Fact]
    public async Task Ignores_closed_goals_in_share_check()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var first = await CreatedAsync(await PostAsync(app, app.AdultA, Goals, Body(app, "Voyage", funding: (envelope, 6000))));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(app, app.AdultA, $"{Goals}/{first.Value}/close")).StatusCode);

        await CreatedAsync(await PostAsync(app, app.AdultA, Goals, Body(app, "Voiture", funding: (envelope, 6000))));
    }

    [Fact]
    public async Task Ignores_other_adults_private_goal_in_share_check()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var hidden = await app.NewGoalAsync("ZZSECRETGOAL", app.AdultB, Visibility.Private, [(envelope, 6000)]);

        var response = await PostAsync(app, app.AdultA, Goals, Body(app, funding: (envelope, 6000)));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.DoesNotContain("ZZSECRETGOAL", text, StringComparison.Ordinal);
        Assert.DoesNotContain(hidden.Value.ToString(), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reopen_refused_over_hundred_percent()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var first = await CreatedAsync(await PostAsync(app, app.AdultA, Goals, Body(app, "Voyage", funding: (envelope, 6000))));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(app, app.AdultA, $"{Goals}/{first.Value}/close")).StatusCode);
        await CreatedAsync(await PostAsync(app, app.AdultA, Goals, Body(app, "Voiture", funding: (envelope, 6000))));

        var refused = await PostAsync(app, app.AdultA, $"{Goals}/{first.Value}/reopen");

        await AssertRefusedAsync(refused, HttpStatusCode.Conflict, GoalsCodes.ShareExceeded);
        Assert.Equal(4000, (await JsonAsync(refused)).GetProperty("freeBp").GetInt32());
    }

    [Fact]
    public async Task Edit_keeps_links_the_editor_cannot_see()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var hidden = await app.NewEnvelopeAsync("PEA de B", app.AdultB, Visibility.Private);
        var goal = await app.NewGoalAsync("Maison", app.AdultA, funding: [(shared, 5000)], owners: [(app.AdultA, 5000), (app.AdultB, 5000)]);
        Assert.Equal(HttpStatusCode.OK, (await EditAsync(app, app.AdultB, goal, Edit("Maison", funding: [(shared, 5000), (hidden, 3000)]))).StatusCode);

        var edited = await EditAsync(app, app.AdultA, goal, Edit("Maison", funding: (shared, 7000)));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.DoesNotContain(hidden.Value.ToString(), await edited.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { new Funding(goal, hidden, 3000), new Funding(goal, shared, 7000) }.OrderBy(f => f.Envelope.Value),
            (await FundingAsync(app, goal)).OrderBy(f => f.Envelope.Value));
    }

    /// <summary>D10: A sees 120 % (their private goal, then B's shared one); renaming keeps the share and passes, raising it does not.</summary>
    [Fact]
    public async Task Rename_allowed_in_over_allocated_state()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        await app.NewGoalAsync("Voyage", app.AdultA, Visibility.Private, [(envelope, 6000)]);
        var shared = await app.NewGoalAsync("Maison", app.AdultB, funding: [(envelope, 6000)], owners: [(app.AdultA, 5000), (app.AdultB, 5000)]);

        Assert.Equal(HttpStatusCode.OK, (await EditAsync(app, app.AdultA, shared, Edit("Maison neuve", funding: (envelope, 6000)))).StatusCode);
        await AssertRefusedAsync(await EditAsync(app, app.AdultA, shared, Edit("Maison neuve", funding: (envelope, 7000))),
            HttpStatusCode.Conflict, GoalsCodes.ShareExceeded);
        Assert.Equal("Maison neuve", (await StoredAsync(app, (_, items) => items.FindAsync(shared, default)))!.DisplayName);
    }

    [Fact]
    public async Task Edit_keeps_an_existing_link_to_a_closed_envelope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var open = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var closing = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var other = await app.NewEnvelopeAsync("CTO", app.AdultA, Visibility.Shared);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA, funding: [(open, 5000), (closing, 5000)]);
        foreach (var envelope in new[] { closing, other })
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(app, app.AdultA, $"/api/v1/investments/envelopes/{envelope.Value}/close",
                new { closedOn = "2025-10-01" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await EditAsync(app, app.AdultA, goal, Edit("Voyage", funding: [(open, 5000), (closing, 2000)]))).StatusCode);
        await AssertRefusedAsync(await EditAsync(app, app.AdultA, goal, Edit("Voyage", funding: [(open, 5000), (closing, 2000), (other, 1000)])), HttpStatusCode.UnprocessableEntity, GoalsCodes.EnvelopeNotVisible);

        Assert.Equal(new[] { new Funding(goal, open, 5000), new Funding(goal, closing, 2000) }.OrderBy(f => f.Envelope.Value),
            (await FundingAsync(app, goal)).OrderBy(f => f.Envelope.Value));
    }

    [Fact]
    public async Task Edit_renames_the_item_and_keeps_visibility()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA, Visibility.Private);

        Assert.Equal(HttpStatusCode.OK, (await EditAsync(app, app.AdultA, goal, Edit("Voyage au Japon", "12000", "2026-12-31"))).StatusCode);

        var (stored, item) = await StoredAsync(app, async (store, items) => ((await store.FindAsync(goal, default))!, (await items.FindAsync(goal, default))!));
        Assert.Equal(("Voyage au Japon", Visibility.Private, app.AdultA), (item.DisplayName, item.Visibility, Assert.Single(item.Owners).PersonId));
        Assert.Equal((12_000m, new LocalDate(2026, 12, 31), GoalKinds.Savings), (stored.TargetAmount!.Value, stored.TargetDate!.Value, stored.Kind));
    }

    [Fact]
    public async Task Stale_edit_answers_409()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA);
        var read = (await StoredAsync(app, (store, _) => store.FindAsync(goal, default)))!.Version;
        var missing = Edit();
        missing["version"] = null;

        var unversioned = await EditAsync(app, app.AdultA, goal, missing);
        await AssertRefusedAsync(unversioned, HttpStatusCode.UnprocessableEntity, "request.invalid");
        Assert.Equal("version", (await JsonAsync(unversioned)).GetProperty("errors")[0].GetProperty("path").GetString());
        Assert.Equal(HttpStatusCode.OK, (await EditAsync(app, app.AdultA, goal, Edit("Voyage", "11000"))).StatusCode);
        var stale = Edit("Voyage", "12000");
        stale["version"] = read;
        await AssertRefusedAsync(await EditAsync(app, app.AdultA, goal, stale), HttpStatusCode.Conflict, GoalsCodes.Conflict);

        Assert.Equal(11_000m, (await StoredAsync(app, (store, _) => store.FindAsync(goal, default)))!.TargetAmount);
    }

    /// <summary>Every edit bumps the goal's version, even one that changes only the funding: the goal row is always written.</summary>
    [Fact]
    public async Task Stale_edit_after_funding_only_change_answers_409()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA);
        Assert.Equal(HttpStatusCode.OK, (await EditAsync(app, app.AdultA, goal, Edit())).StatusCode); // the note, so the row now matches Edit()
        var read = (await StoredAsync(app, (store, _) => store.FindAsync(goal, default)))!.Version;

        var fundingOnly = Edit(funding: (envelope, 5000));
        fundingOnly["version"] = read;
        Assert.Equal(HttpStatusCode.OK, (await EditAsync(app, app.AdultA, goal, fundingOnly)).StatusCode);
        var stale = Edit("Voyage", "12000");
        stale["version"] = read;

        await AssertRefusedAsync(await EditAsync(app, app.AdultA, goal, stale), HttpStatusCode.Conflict, GoalsCodes.Conflict);
        Assert.Equal(10_000m, (await StoredAsync(app, (store, _) => store.FindAsync(goal, default)))!.TargetAmount);
    }

    [Fact]
    public async Task Closed_goal_refuses_edit_and_progress()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA);
        var url = $"{Goals}/{goal.Value}";
        await AssertRefusedAsync(await PostAsync(app, app.AdultA, $"{url}/reopen"), HttpStatusCode.Conflict, GoalsCodes.GoalOpen);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(app, app.AdultA, $"{url}/close")).StatusCode);

        await AssertRefusedAsync(await PostAsync(app, app.AdultA, $"{url}/close"), HttpStatusCode.Conflict, GoalsCodes.GoalClosed);
        await AssertRefusedAsync(await EditAsync(app, app.AdultA, goal, Edit()), HttpStatusCode.Conflict, GoalsCodes.GoalClosed);
        await AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/progress/2025-10-01", new { amount = "100" }),
            HttpStatusCode.Conflict, GoalsCodes.GoalClosed);
        await AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{url}/progress/2025-10-01"), HttpStatusCode.Conflict, GoalsCodes.GoalClosed);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(app, app.AdultA, $"{url}/reopen")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EditAsync(app, app.AdultA, goal, Edit())).StatusCode);
    }

    [Fact]
    public async Task Progress_entry_put_replaces_and_delete_removes()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA);
        var entry = $"{Goals}/{goal.Value}/progress/2025-10-01";

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, entry, new { amount = "100" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, entry, new { amount = "150.50" })).StatusCode);
        Assert.Equal(new GoalEntry(goal, new LocalDate(2025, 10, 1), 150.50m),
            Assert.Single(await StoredAsync(app, (store, _) => store.EntriesOfAsync([goal], default))));

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, entry)).StatusCode);
        Assert.Empty(await StoredAsync(app, (store, _) => store.EntriesOfAsync([goal], default)));
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Delete, entry)).StatusCode);
    }

    [Fact]
    public async Task Progress_refused_in_future_and_on_funded_goal()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var free = await app.NewGoalAsync("Voyage", app.AdultA);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var funded = await app.NewGoalAsync("Voiture", app.AdultA, funding: [(envelope, 5000)]);

        await AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Goals}/{free.Value}/progress/2025-10-16", new { amount = "100" }),
            HttpStatusCode.UnprocessableEntity, GoalsCodes.ProgressInFuture);
        await AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Goals}/{free.Value}/progress/2025-10-15", new { amount = "-1" }),
            HttpStatusCode.UnprocessableEntity, GoalsCodes.InvalidTarget);
        await AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Goals}/{funded.Value}/progress/2025-10-15", new { amount = "100" }),
            HttpStatusCode.UnprocessableEntity, GoalsCodes.MixedFunding);
        Assert.Empty(await StoredAsync(app, (store, _) => store.EntriesOfAsync([free, funded], default)));
    }

    [Fact]
    public async Task Reader_cannot_write_a_goal_they_only_read()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA);
        var url = $"{Goals}/{goal.Value}";

        Assert.Equal(HttpStatusCode.Forbidden, (await EditAsync(app, app.AdultB, goal, Edit("Pris"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(app, app.AdultB, $"{url}/close")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Put, $"{url}/progress/2025-10-01", new { amount = "1" })).StatusCode);
        Assert.Equal("Voyage", (await StoredAsync(app, (_, items) => items.FindAsync(goal, default)))!.DisplayName);
    }
}

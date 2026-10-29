using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Income.Application;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Audit;
using Nid.TestSupport;
using NodaTime;
using NodaTime.Text;

namespace Nid.Income.IntegrationTests;

public class OneOffAndClosingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Sources = "/api/v1/income/sources";

    private static object Phase(string label = "Salaire", string startsOn = "2025-01-01", string amount = "2000", string? endsOn = null,
        int count = 1, string unit = "month", int? maxCycles = null)
        => new { label, startsOn, endsOn, netAmount = new { amount, currency = "EUR" }, every = new { count, unit }, maxCycles };

    private static object OneOff(string occurredOn = "2025-06-10", string amount = "500", string currency = "EUR", string? note = "Prime")
        => new { occurredOn, amount = new { amount, currency }, note };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<Guid> CreatedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
        => (await JsonAsync(response)).GetProperty("code").GetString();

    private static async Task<Guid> NewSourceAsync(NidApp app, params object[] phases)
        => await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Sources, new Dictionary<string, object?>
        {
            ["displayName"] = "Salaire", ["kind"] = "salary", ["currency"] = "EUR", ["visibility"] = "shared",
            ["owners"] = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            ["phases"] = phases,
        }));

    private static async Task<JsonElement> DetailAsync(NidApp app, Guid id)
        => await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{id}"));

    private static async Task<JsonElement> ExpectedAsync(NidApp app, Guid id, string from, string to)
        => await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{id}/expected?from={from}&to={to}"));

    private static decimal Amount(JsonElement money) => decimal.Parse(money.GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);

    private static IEnumerable<Guid> OneOffIds(JsonElement detail) => detail.GetProperty("oneOffs").EnumerateArray().Select(o => o.GetProperty("id").GetGuid());

    private static async Task<List<(Guid Id, string Label, LocalDate StartsOn, LocalDate? EndsOn)>> StoredAsync(NidApp app, Guid source)
    {
        await using var income = PostgresFixture.NewIncomeContext(app.ConnectionString);
        return (await income.Phases.Where(p => p.SourceItemId == source).ToListAsync())
            .Select(p => (p.Id, p.Label, p.StartsOn, p.EndsOn)).OrderBy(p => p.Label, StringComparer.Ordinal).ToList();
    }

    private static async Task<List<(string Label, string? EndsOn)>> EndsAsync(NidApp app, Guid source)
        => (await StoredAsync(app, source)).Select(p => (p.Label, p.EndsOn is { } e ? LocalDatePattern.Iso.Format(e) : null)).ToList();

    /// <summary>A Core flow on the item that is not a one-off: an income without category, recorded through IFlows as a person.</summary>
    private static async Task<Guid> RecordUncategorisedAsync(NidApp app, Guid source)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
        var flow = (await scope.ServiceProvider.GetRequiredService<IFlows>().RecordAsync(
            new NewFlow(new ItemId(source), new LocalDate(2025, 6, 20), FlowKind.Income, Money.Create(70m, Currency.Eur).Ok(), null, null, "autre"),
            default)).Ok();
        return flow.Id;
    }

    [Fact]
    public async Task A_one_off_counts_in_its_month_and_not_in_the_monthly_equivalent()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase());

        var fid = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/one-offs", OneOff()));

        var expected = await ExpectedAsync(app, id, "2025-06-01", "2025-06-30");
        Assert.Equal(2500m, Amount(Assert.Single(expected.GetProperty("months").EnumerateArray()).GetProperty("total")));
        var list = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}?scope=me"));
        Assert.Equal("2000.00", Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("monthlyEquivalent").GetProperty("amount").GetString());
        var detail = await DetailAsync(app, id);
        Assert.Equal(fid, Assert.Single(OneOffIds(detail)));
        Assert.Equal("Prime", detail.GetProperty("oneOffs")[0].GetProperty("note").GetString());
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var row = await core.Flows.SingleAsync(f => f.Id == fid);
        Assert.Equal(("income", "one_off"), (row.Kind, row.Category));
    }

    private static object TaxedOneOff(string? taxable)
        => new
        {
            occurredOn = "2025-06-10", amount = new { amount = "4812.50", currency = "EUR" }, note = "Prime",
            taxableAmount = taxable is null ? null : new { amount = taxable, currency = "EUR" },
        };

    private static async Task<int> TaxableRowsAsync(NidApp app)
    {
        await using var income = PostgresFixture.NewIncomeContext(app.ConnectionString);
        return await income.Database.SqlQuery<int>($"select count(*)::int as \"Value\" from income.one_off_taxable").SingleAsync();
    }

    [Fact]
    public async Task A_one_off_keeps_its_taxable_amount_and_loses_it_when_corrected_without()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase());

        var fid = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/one-offs", TaxedOneOff("5150.00")));

        var oneOff = (await DetailAsync(app, id)).GetProperty("oneOffs")[0];
        Assert.Equal(5150.00m, Amount(oneOff.GetProperty("taxableAmount")));
        Assert.Equal("EUR", oneOff.GetProperty("taxableAmount").GetProperty("currency").GetString());

        // Corrected with another taxable amount: the new one-off carries it, and the retired id keeps no row.
        var next = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Sources}/{id}/one-offs/{fid}", TaxedOneOff("5000")))).GetProperty("id").GetGuid();
        Assert.Equal(5000m, Amount((await DetailAsync(app, id)).GetProperty("oneOffs")[0].GetProperty("taxableAmount")));
        Assert.Equal(1, await TaxableRowsAsync(app));

        // Corrected without: no taxable amount is left.
        var last = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Sources}/{id}/one-offs/{next}", TaxedOneOff(null)))).GetProperty("id").GetGuid();
        var corrected = Assert.Single((await DetailAsync(app, id)).GetProperty("oneOffs").EnumerateArray());
        Assert.Equal(last, corrected.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, corrected.GetProperty("taxableAmount").ValueKind);
        Assert.Equal(0, await TaxableRowsAsync(app));
    }

    [Fact]
    public async Task A_one_off_taxable_amount_is_positive_and_in_the_source_currency()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase());

        var zero = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/one-offs", TaxedOneOff("0"));
        var dollars = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/one-offs", new
        {
            occurredOn = "2025-06-10", amount = new { amount = "500", currency = "EUR" }, taxableAmount = new { amount = "400", currency = "USD" },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, zero.StatusCode);
        Assert.Equal("income.invalid_taxable", await CodeAsync(zero));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, dollars.StatusCode);
        Assert.Equal(CoreFactCodes.CurrencyMismatch, await CodeAsync(dollars));
        Assert.Empty(OneOffIds(await DetailAsync(app, id)));
        Assert.Equal(0, await TaxableRowsAsync(app));
    }

    [Fact]
    public async Task Deleting_a_one_off_removes_its_taxable_amount()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase());
        var fid = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/one-offs", TaxedOneOff("5150.00")));
        Assert.Equal(1, await TaxableRowsAsync(app));

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Sources}/{id}/one-offs/{fid}")).StatusCode);

        Assert.Equal(0, await TaxableRowsAsync(app));
    }

    [Fact]
    public async Task A_one_off_is_corrected_and_deleted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase());
        var fid = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/one-offs", OneOff()));

        var corrected = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Sources}/{id}/one-offs/{fid}", OneOff("2025-06-12", "600"));

        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        var next = (await JsonAsync(corrected)).GetProperty("id").GetGuid();
        Assert.NotEqual(fid, next);
        var detail = await DetailAsync(app, id);
        Assert.Equal(next, Assert.Single(OneOffIds(detail)));
        Assert.Equal(600m, Amount(detail.GetProperty("oneOffs")[0].GetProperty("amount")));

        // A flow of another category on the item is not a one-off: it answers as an unknown one, and stays.
        var other = await RecordUncategorisedAsync(app, id);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Sources}/{id}/one-offs/{other}", OneOff())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Sources}/{id}/one-offs/{other}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Sources}/{id}/one-offs/{fid}")).StatusCode); // the retired one
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            Assert.Null((await core.Flows.SingleAsync(f => f.Id == other)).RetiredAt);

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Sources}/{id}/one-offs/{next}")).StatusCode);
        Assert.Empty(OneOffIds(await DetailAsync(app, id)));
    }

    [Fact]
    public async Task A_one_off_follows_the_Cores_rules()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase());

        async Task AssertRefusedAsync(object body, string code)
        {
            var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/one-offs", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(code, await CodeAsync(response));
        }

        await AssertRefusedAsync(OneOff(amount: "0"), "core.amount_not_positive");
        await AssertRefusedAsync(OneOff("2025-10-16"), "core.date_in_future");
        await AssertRefusedAsync(OneOff(currency: "USD"), "core.currency_mismatch");
        Assert.Empty(OneOffIds(await DetailAsync(app, id)));

        // A missing member answers 422 at its path, never a crash.
        var missing = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/one-offs", new { note = "x" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal(["amount", "occurredOn"],
            (await JsonAsync(missing)).GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("path").GetString()!).Order().ToList());
    }

    [Fact]
    public async Task Ending_a_source_cuts_its_running_phases_and_archives_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase("Salaire"), Phase("Prime", amount: "300", endsOn: "2026-12-15"));

        var closed = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/close", new { endedOn = "2025-09-30" });

        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        Assert.Equal([("Prime", "2025-09-30"), ("Salaire", "2025-09-30")], await EndsAsync(app, id));
        var list = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}?scope=me"));
        Assert.Empty(list.GetProperty("items").EnumerateArray());
        var archived = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}?scope=me&archived=true"));
        var row = Assert.Single(archived.GetProperty("items").EnumerateArray());
        Assert.True(row.GetProperty("ended").GetBoolean());
        Assert.Equal("2025-09-30", row.GetProperty("endedOn").GetString());

        await AssertEveryWriteEndedAsync(app, id);
    }

    /// <summary>
    /// Controller ruling (a): every write of an ended source answers 409 income.source_ended and changes nothing; a one-off
    /// is refused only when dated after the end (issue #22).
    /// </summary>
    private static async Task AssertEveryWriteEndedAsync(NidApp app, Guid id)
    {
        var before = await StoredAsync(app, id);
        var phase = before[0].Id;
        var unknown = Guid.NewGuid();
        var oneOff = OneOff("2025-10-01");
        var phaseBody = new
        {
            label = "Salaire", startsOn = "2025-01-01", netAmount = new { amount = "1", currency = "EUR" },
            every = new { count = 1, unit = "month" }, version = 1,
        };
        var routes = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Post, $"{Sources}/{id}/one-offs", oneOff),
            (HttpMethod.Put, $"{Sources}/{id}/one-offs/{unknown}", oneOff),
            (HttpMethod.Post, $"{Sources}/{id}/phases", Phase("Autre")),
            (HttpMethod.Put, $"{Sources}/{id}/phases/{phase}", phaseBody),
            (HttpMethod.Delete, $"{Sources}/{id}/phases/{phase}?version=1", null),
            (HttpMethod.Post, $"{Sources}/{id}/phase-changes", new { label = "Salaire", effectiveOn = "2025-06-01", netAmount = new { amount = "1", currency = "EUR" } }),
            (HttpMethod.Patch, $"{Sources}/{id}", new { note = "x" }),
            (HttpMethod.Post, $"{Sources}/{id}/close", new { endedOn = "2025-09-30" }),
        };
        foreach (var (method, url, body) in routes)
        {
            var response = await app.RawAsync(app.AdultA, method, url, body);
            Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{method} {url} answered {response.StatusCode}");
            Assert.Equal("income.source_ended", await CodeAsync(response));
        }
        Assert.Equal(before, await StoredAsync(app, id));
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Equal(0, await core.Flows.CountAsync(f => f.ItemId == id));
    }

    [Fact]
    public async Task An_ended_source_takes_one_offs_up_to_its_end() // Issue #22
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase());
        var kept = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/one-offs", OneOff("2025-05-02")));
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/close", new { endedOn = "2025-09-30" });

        var added = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/one-offs", OneOff("2025-09-30")));
        var corrected = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Sources}/{id}/one-offs/{kept}", OneOff("2025-05-03", "450"));
        var late = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Sources}/{id}/one-offs/{added}", OneOff("2025-10-01"));

        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("income.source_ended", await CodeAsync(late));
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Sources}/{id}/one-offs/{added}")).StatusCode);
        var detail = await DetailAsync(app, id);
        Assert.Equal((await JsonAsync(corrected)).GetProperty("id").GetGuid(), Assert.Single(OneOffIds(detail)));
        Assert.True(detail.GetProperty("ended").GetBoolean());
    }

    [Fact]
    public async Task The_detail_lists_older_one_offs_on_request() // Issue #22
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase("Salaire", "2023-01-01"));
        var old = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/one-offs", OneOff("2023-03-01")));
        var recent = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/one-offs", OneOff("2025-06-10")));

        async Task<List<Guid>> ListedAsync(string query) => OneOffIds(await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{id}{query}"))).Order().ToList();

        Assert.Equal([recent], await ListedAsync(""));
        Assert.Equal(new[] { old, recent }.Order().ToList(), await ListedAsync("?from=2000-01-01"));
        Assert.Equal([old], await ListedAsync("?to=2023-12-31"));
        Assert.Equal([old], await ListedAsync("?from=2023-03-01&to=2023-03-01"));
        var reversed = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{id}?from=2024-01-01&to=2023-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
        Assert.Equal("income.invalid_period", await CodeAsync(reversed));
    }

    [Fact]
    public async Task Ending_is_refused_while_a_phase_starts_later() // Review Focus #4
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase("Salaire"), Phase("Futur", "2025-10-01", amount: "2500"));
        var before = await StoredAsync(app, id);

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/close", new { endedOn = "2025-09-30" });
        var (_, failure) = await app.InvokeAsync(app.AdultA, new CloseIncomeSourceCommand(new ItemId(id), new LocalDate(2025, 9, 30)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("income.invalid_source", await CodeAsync(refused));
        Assert.Equal(("income.invalid_source", "endedOn"), (failure?.Code, failure!.Metadata!["field"]));
        // A phase starting on the very day would be empty once cut there: refused the same way.
        var onTheDay = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/close", new { endedOn = "2025-10-01" });
        Assert.Equal("income.invalid_source", await CodeAsync(onTheDay));
        Assert.Equal(before, await StoredAsync(app, id));
        var list = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}?scope=me"));
        Assert.Single(list.GetProperty("items").EnumerateArray());

        var future = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/close", new { endedOn = "2025-10-16" });
        var (_, futureFailure) = await app.InvokeAsync(app.AdultA, new CloseIncomeSourceCommand(new ItemId(id), new LocalDate(2025, 10, 16)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, future.StatusCode);
        Assert.Equal("core.date_in_future", await CodeAsync(future));
        Assert.Equal(("core.date_in_future", "endedOn"), (futureFailure?.Code, futureFailure!.Metadata!["field"]));
        Assert.Equal(before, await StoredAsync(app, id));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/close", new { })).StatusCode);
    }

    [Fact]
    public async Task Reopening_keeps_the_phases_as_they_are()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase("Salaire"));
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/close", new { endedOn = "2025-09-30" });

        var reopened = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/reopen");

        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        var list = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}?scope=me"));
        var row = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.False(row.GetProperty("ended").GetBoolean());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("endedOn").ValueKind);
        Assert.Equal([("Salaire", "2025-09-30")], await EndsAsync(app, id));

        var again = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/reopen");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("income.source_open", await CodeAsync(again));
    }

    [Fact]
    public async Task Ending_leaves_a_capped_phase_that_is_already_over_and_cuts_one_still_running()
    {
        await using var app = await NidApp.StartAsync(fixture);
        // Over on 2025-04-01 (3 monthly occurrences from 2025-01-01); and running to 2025-12-01 without a stop of its own.
        var id = await NewSourceAsync(app, Phase("Court", maxCycles: 3), Phase("Long", maxCycles: 11));

        var closed = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/close", new { endedOn = "2025-09-30" });

        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        Assert.Equal([("Court", null), ("Long", "2025-09-30")], await EndsAsync(app, id));
    }

    [Fact]
    public async Task A_capped_phase_stops_after_its_last_occurrence_in_the_detail()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase("Prime", "2023-01-15", "1800", unit: "year", maxCycles: 2));

        var detail = await DetailAsync(app, id);

        var phase = detail.GetProperty("phases")[0];
        Assert.Equal("past", phase.GetProperty("status").GetString());
        Assert.Equal("0.00", detail.GetProperty("monthlyEquivalent").GetProperty("amount").GetString());
        var expected = await ExpectedAsync(app, id, "2025-01-01", "2025-03-31");
        Assert.All(expected.GetProperty("months").EnumerateArray(), m => Assert.Empty(m.GetProperty("occurrences").EnumerateArray()));
    }

    [Fact]
    public async Task Deleting_a_phase_without_its_version_is_refused_at_the_field() // Controller ruling (c)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase());
        var pid = Assert.Single(await StoredAsync(app, id)).Id;

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Sources}/{id}/phases/{pid}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains((await JsonAsync(refused)).GetProperty("errors").EnumerateArray(), e => e.GetProperty("path").GetString() == "version");
        Assert.Single(await StoredAsync(app, id));
    }

    [Fact]
    public async Task An_income_flow_without_the_one_off_category_is_not_a_one_off() // Controller ruling (d)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase());
        await RecordUncategorisedAsync(app, id);

        Assert.Empty(OneOffIds(await DetailAsync(app, id)));
        var expected = await ExpectedAsync(app, id, "2025-06-01", "2025-06-30");
        var month = Assert.Single(expected.GetProperty("months").EnumerateArray());
        Assert.Empty(month.GetProperty("oneOffs").EnumerateArray());
        Assert.Equal(2000m, Amount(month.GetProperty("total")));
    }

    [Fact]
    public async Task Another_adults_private_source_answers_like_an_unused_id()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await app.NewIncomeSourceAsync("Secret de B", Visibility.Private, (app.AdultB, 10_000));
        var unused = Guid.NewGuid();
        var flow = Guid.NewGuid();

        async Task<List<(HttpStatusCode, string?)>> AnswersAsync(Guid source)
        {
            var answers = new List<(HttpStatusCode, string?)>();
            foreach (var (method, url, body) in new (HttpMethod, string, object?)[]
            {
                (HttpMethod.Post, $"{Sources}/{source}/one-offs", OneOff()),
                (HttpMethod.Put, $"{Sources}/{source}/one-offs/{flow}", OneOff()),
                (HttpMethod.Delete, $"{Sources}/{source}/one-offs/{flow}", null),
                (HttpMethod.Post, $"{Sources}/{source}/close", new { endedOn = "2025-09-30" }),
                (HttpMethod.Post, $"{Sources}/{source}/reopen", null),
            })
            {
                var response = await app.RawAsync(app.AdultA, method, url, body);
                answers.Add((response.StatusCode, await response.Content.ReadAsStringAsync() is var text && text.Contains("\"code\"") ? await CodeAsync(response) : null));
            }
            return answers;
        }

        var hiddenAnswers = await AnswersAsync(hidden.Value);
        Assert.All(hiddenAnswers, a => Assert.Equal(HttpStatusCode.NotFound, a.Item1));
        Assert.Equal(await AnswersAsync(unused), hiddenAnswers);
    }

    [Fact]
    public async Task A_phase_after_a_finished_capped_phase_of_its_label_is_accepted_and_one_before_its_end_is_not()
    {
        await using var app = await NidApp.StartAsync(fixture);
        // Occurrences 2023-01-15 and 2024-01-15: effective end 2025-01-15.
        var id = await NewSourceAsync(app, Phase("Prime", "2023-01-15", "1800", unit: "year", maxCycles: 2));

        var early = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/phases", Phase("Prime", "2024-12-31", "1900", unit: "year"));
        Assert.Equal("income.phase_overlap", await CodeAsync(early));
        var after = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/phases", Phase("Prime", "2025-01-15", "1900", unit: "year"));

        await CreatedAsync(after);
        Assert.Equal(2, (await StoredAsync(app, id)).Count);
        // The same at creation: a source created with both phases.
        await NewSourceAsync(app, Phase("Prime", "2023-01-15", "1800", unit: "year", maxCycles: 2), Phase("Prime", "2025-01-15", "1900", unit: "year"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1201)]
    [InlineData(int.MaxValue)]
    public async Task The_cap_is_bounded_to_1_through_1200(int maxCycles)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app);

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/phases", Phase(maxCycles: maxCycles));
        var (_, failure) = await app.InvokeAsync(app.AdultA, new AddIncomePhaseCommand(new ItemId(id),
            new PhaseDraft("Salaire", new LocalDate(2025, 1, 1), null, Money.Create(1m, Currency.Eur).Ok(),
                Nid.Core.Contracts.Duration.Create(1, DurationUnit.Month).Ok(), null, maxCycles, null)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("income.invalid_phase", await CodeAsync(refused));
        Assert.Equal("maxCycles", failure!.Metadata!["field"]);
        Assert.Empty(await StoredAsync(app, id));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/phases", Phase(maxCycles: 1200)));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_change_of_phase_inside_a_capped_phase_ends_where_the_cap_did()
    {
        await using var app = await NidApp.StartAsync(fixture);
        // Monthly from 2025-01-01, 12 occurrences: effective end 2026-01-01.
        var id = await NewSourceAsync(app, Phase("Salaire", amount: "2000", maxCycles: 12));

        var next = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/phase-changes",
            new { label = "Salaire", effectiveOn = "2025-07-01", netAmount = new { amount = "2100", currency = "EUR" } }));

        var stored = await StoredAsync(app, id);
        Assert.Equal(new LocalDate(2025, 7, 1), stored.Single(p => p.Id != next).EndsOn);
        var opened = stored.Single(p => p.Id == next);
        Assert.Equal((new LocalDate(2025, 7, 1), (LocalDate?)new LocalDate(2026, 1, 1)), (opened.StartsOn, opened.EndsOn));
    }
}

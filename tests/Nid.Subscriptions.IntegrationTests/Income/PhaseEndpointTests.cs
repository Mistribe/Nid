using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Income.Application;
using Nid.TestSupport;
using NodaTime;
using NodaTime.Text;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Subscriptions.IntegrationTests.Income;

[Collection("postgres")]
public class PhaseEndpointTests(PostgresFixture fixture)
{
    private const string Sources = "/api/v1/income/sources";

    private static object Phase(string label = "Salaire", string startsOn = "2025-01-01", string amount = "2300", string? endsOn = null,
        int count = 1, string unit = "month")
        => new { label, startsOn, endsOn, netAmount = new { amount, currency = "EUR" }, every = new { count, unit } };

    private static Dictionary<string, object?> Source(NidApp app, params object[] phases)
        => new()
        {
            ["displayName"] = "Salaire", ["kind"] = "salary", ["currency"] = "EUR", ["visibility"] = "shared",
            ["owners"] = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            ["phases"] = phases,
        };

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
        => await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Sources, Source(app, phases)));

    private static async Task<JsonElement> DetailAsync(NidApp app, Guid id)
        => await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{id}"));

    private static JsonElement PhaseOf(JsonElement detail, Guid phase)
        => detail.GetProperty("phases").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == phase);

    private static decimal Amount(JsonElement money) => decimal.Parse(money.GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);

    private static LocalDate Date(string iso) => LocalDatePattern.Iso.Parse(iso).Value;

    private static async Task<List<(Guid Id, string Label, LocalDate StartsOn, LocalDate? EndsOn, decimal Net)>> StoredAsync(NidApp app, Guid source)
    {
        await using var income = PostgresFixture.NewIncomeContext(app.ConnectionString);
        return (await income.Phases.Where(p => p.SourceItemId == source).ToListAsync())
            .Select(p => (p.Id, p.Label, p.StartsOn, p.EndsOn, p.NetAmount)).OrderBy(p => p.Id).ToList();
    }

    [Fact]
    public async Task A_phase_is_added_corrected_and_deleted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app);

        var pid = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/phases", Phase(amount: "2400")));

        var phase = PhaseOf(await DetailAsync(app, id), pid);
        Assert.Equal("running", phase.GetProperty("status").GetString());
        Assert.Equal("2400.00", phase.GetProperty("monthlyEquivalent").GetProperty("amount").GetString());
        Assert.Equal(2400m, Amount(phase.GetProperty("netAmount")));
        var version = phase.GetProperty("version").GetUInt32();

        var body = new Dictionary<string, object?>
        {
            ["label"] = "Salaire", ["startsOn"] = "2025-01-01", ["netAmount"] = new { amount = "2500", currency = "EUR" },
            ["every"] = new { count = 1, unit = "month" }, ["version"] = version,
        };
        var corrected = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Sources}/{id}/phases/{pid}", body);
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        var after = PhaseOf(await DetailAsync(app, id), pid);
        Assert.Equal(2500m, Amount(after.GetProperty("netAmount")));
        Assert.NotEqual(version, after.GetProperty("version").GetUInt32());

        // The version read before the correction no longer matches: someone changed the phase since.
        body["netAmount"] = new { amount = "9999", currency = "EUR" };
        var stale = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Sources}/{id}/phases/{pid}", body);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("income.phase_conflict", await CodeAsync(stale));
        Assert.Equal(2500m, Assert.Single(await StoredAsync(app, id)).Net);

        var staleDelete = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Sources}/{id}/phases/{pid}?version={version}");
        Assert.Equal(HttpStatusCode.Conflict, staleDelete.StatusCode);
        var deleted = await app.RawAsync(app.AdultA, HttpMethod.Delete,
            $"{Sources}/{id}/phases/{pid}?version={after.GetProperty("version").GetUInt32()}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await StoredAsync(app, id));
        Assert.Empty((await DetailAsync(app, id)).GetProperty("phases").EnumerateArray());

        var unknown = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Sources}/{id}/phases/{unknown}", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Sources}/{id}/phases/{unknown}?version=1")).StatusCode);
    }

    [Fact]
    public async Task A_correction_without_its_version_is_refused_at_the_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase());
        var pid = Assert.Single(await StoredAsync(app, id)).Id;

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Sources}/{id}/phases/{pid}", Phase(amount: "2500"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains((await JsonAsync(refused)).GetProperty("errors").EnumerateArray(), e => e.GetProperty("path").GetString() == "version");
        Assert.Equal(2300m, Assert.Single(await StoredAsync(app, id)).Net);
    }

    [Fact]
    public async Task An_overlapping_phase_of_the_same_label_is_refused_and_another_label_runs_in_parallel()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase("Salaire", "2025-01-01"));

        var overlapping = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/phases", Phase("Salaire", "2025-06-01", "2400"));
        var (_, failure) = await app.InvokeAsync(app.AdultA, new AddIncomePhaseCommand(new ItemId(id),
            new PhaseDraft("Salaire", Date("2025-06-01"), null, Money.Create(2400m, Currency.Eur).Ok(),
                Duration.Create(1, DurationUnit.Month).Ok(), null, null, null)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, overlapping.StatusCode);
        Assert.Equal("income.phase_overlap", await CodeAsync(overlapping));
        Assert.Equal("income.phase_overlap", failure?.Code);
        Assert.Equal("startsOn", failure!.Metadata!["field"]);
        Assert.Single(await StoredAsync(app, id));

        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/phases", Phase("Prime", "2025-01-01", "150")));
        Assert.Equal(2, (await StoredAsync(app, id)).Count);
    }

    [Fact]
    public async Task A_raise_closes_the_running_phase_and_opens_the_next()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase("Salaire", "2024-09-01", "2200"));
        var old = Assert.Single(await StoredAsync(app, id)).Id;

        var next = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/phase-changes",
            new { label = "Salaire", effectiveOn = "2025-09-01", netAmount = new { amount = "2300", currency = "EUR" } }));

        var detail = await DetailAsync(app, id);
        var closed = PhaseOf(detail, old);
        Assert.Equal("2025-09-01", closed.GetProperty("endsOn").GetString());
        Assert.Equal("past", closed.GetProperty("status").GetString());
        var opened = PhaseOf(detail, next);
        Assert.Equal("Salaire", opened.GetProperty("label").GetString());
        Assert.Equal("2025-09-01", opened.GetProperty("startsOn").GetString());
        Assert.Equal(JsonValueKind.Null, opened.GetProperty("endsOn").ValueKind);
        Assert.Equal(1, opened.GetProperty("every").GetProperty("count").GetInt32());
        Assert.Equal("month", opened.GetProperty("every").GetProperty("unit").GetString());
        Assert.Equal("running", opened.GetProperty("status").GetString());
        Assert.Equal(2300m, Amount(opened.GetProperty("netAmount")));

        var list = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}?scope=me"));
        var row = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal("2300.00", row.GetProperty("monthlyEquivalent").GetProperty("amount").GetString());
    }

    [Fact]
    public async Task A_raise_on_the_phases_own_start_or_without_a_running_phase_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase("Salaire", "2024-09-01", "2200"), Phase("CDD", "2025-01-01", "1500", endsOn: "2025-06-01"));
        var before = await StoredAsync(app, id);

        ChangeIncomePhaseCommand Change(string label, string effectiveOn)
            => new(new ItemId(id), label, Date(effectiveOn), Money.Create(2300m, Currency.Eur).Ok(), null, null, null);
        async Task<HttpResponseMessage> PostAsync(string label, string effectiveOn)
            => await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/phase-changes",
                new { label, effectiveOn, netAmount = new { amount = "2300", currency = "EUR" } });

        var onStart = await PostAsync("Salaire", "2024-09-01");
        var (_, onStartFailure) = await app.InvokeAsync(app.AdultA, Change("Salaire", "2024-09-01"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, onStart.StatusCode);
        Assert.Equal("income.invalid_phase", await CodeAsync(onStart));
        Assert.Equal("income.invalid_phase", onStartFailure?.Code);
        Assert.Equal("effectiveOn", onStartFailure!.Metadata!["field"]);
        Assert.Equal(before, await StoredAsync(app, id));

        var unknown = await PostAsync("Inconnu", "2025-09-01");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Equal("income.no_running_phase", await CodeAsync(unknown));
        Assert.Equal(before, await StoredAsync(app, id));

        // The fixed-term phase ended on 2025-06-01 (excluded): nothing of that label runs on or after it.
        foreach (var effectiveOn in new[] { "2025-06-01", "2025-07-01" })
        {
            var ended = await PostAsync("CDD", effectiveOn);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, ended.StatusCode);
            Assert.Equal("income.no_running_phase", await CodeAsync(ended));
        }
        var (_, endedFailure) = await app.InvokeAsync(app.AdultA, Change("CDD", "2025-07-01"));
        Assert.Equal("income.no_running_phase", endedFailure?.Code);
        Assert.Equal(before, await StoredAsync(app, id));
    }

    [Fact]
    public async Task A_raise_inside_a_fixed_term_phase_keeps_its_end_and_never_its_max_cycles()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, new
        {
            label = "CDD", startsOn = "2025-01-01", endsOn = "2025-12-01", maxCycles = 11,
            netAmount = new { amount = "1500", currency = "EUR" }, every = new { count = 1, unit = "month" },
        });

        var next = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/phase-changes",
            new { label = " CDD ", effectiveOn = "2025-07-01", netAmount = new { amount = "1600", currency = "EUR" }, every = new { count = 3, unit = "month" } }));

        var opened = PhaseOf(await DetailAsync(app, id), next);
        Assert.Equal("2025-12-01", opened.GetProperty("endsOn").GetString());
        Assert.Equal(JsonValueKind.Null, opened.GetProperty("maxCycles").ValueKind);
        Assert.Equal(3, opened.GetProperty("every").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Expected_income_is_given_month_by_month()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase("Salaire", "2025-01-28", "2000"), Phase("Prime", "2024-12-15", "1800", unit: "year"));

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{id}/expected?from=2025-01-01&to=2025-12-31");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expected = await JsonAsync(response);
        Assert.Equal(id, expected.GetProperty("id").GetGuid());
        var months = expected.GetProperty("months").EnumerateArray().ToList();
        Assert.Equal(12, months.Count);
        Assert.Equal((2025, 1), (months[0].GetProperty("year").GetInt32(), months[0].GetProperty("month").GetInt32()));
        Assert.Equal(2000m, Amount(months[0].GetProperty("total")));
        Assert.Equal("EUR", months[0].GetProperty("total").GetProperty("currency").GetString());
        var january = Assert.Single(months[0].GetProperty("occurrences").EnumerateArray());
        Assert.Equal(("Salaire", "2025-01-28"), (january.GetProperty("label").GetString(), january.GetProperty("on").GetString()));
        Assert.Equal(3800m, Amount(months[11].GetProperty("total")));
        Assert.Equal(["2025-12-15", "2025-12-28"],
            months[11].GetProperty("occurrences").EnumerateArray().Select(o => o.GetProperty("on").GetString()));

        var reversed = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{id}/expected?from=2025-02-01&to=2025-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
        Assert.Equal("income.invalid_period", await CodeAsync(reversed));
        var tooLong = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{id}/expected?from=2025-01-01&to=2027-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("income.invalid_period", await CodeAsync(tooLong));
        Assert.Equal(HttpStatusCode.OK,
            (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{id}/expected?from=2025-01-01&to=2026-12-31")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{id}/expected?from=2025-01-01")).StatusCode);
    }

    [Fact]
    public async Task The_detail_warns_of_a_gap_within_60_days()
    {
        await using var app = await NidApp.StartAsync(fixture);
        LocalDate today;
        await using (var scope = app.Services.CreateAsyncScope())
            today = await scope.ServiceProvider.GetRequiredService<IHouseholdCalendar>().TodayAsync(CancellationToken.None);
        var endsOn = LocalDatePattern.Iso.Format(today.PlusDays(30));
        var id = await NewSourceAsync(app, Phase("Contrat", "2025-01-01", "1800", endsOn: endsOn));

        var gap = Assert.Single((await DetailAsync(app, id)).GetProperty("gapSoon").EnumerateArray());
        Assert.Equal(("Contrat", endsOn), (gap.GetProperty("label").GetString(), gap.GetProperty("endsOn").GetString()));
        var list = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}?scope=me"));
        Assert.Equal(["Contrat"], Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("gapSoon").EnumerateArray().Select(g => g.GetString()));

        var successor = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/phases", Phase("Contrat", endsOn, "1900")));

        var detail = await DetailAsync(app, id);
        Assert.Empty(detail.GetProperty("gapSoon").EnumerateArray());
        Assert.Equal("upcoming", PhaseOf(detail, successor).GetProperty("status").GetString());
        list = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}?scope=me"));
        Assert.Empty(Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("gapSoon").EnumerateArray());
    }

    [Fact]
    public async Task Phases_of_another_adults_private_source_are_not_found()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await app.NewIncomeSourceAsync("Secret de B", Visibility.Private, (app.AdultB, 10_000));
        var unused = Guid.NewGuid();
        var pid = Assert.Single(await StoredAsync(app, hidden.Value)).Id;
        var correction = new
        {
            label = "Salaire", startsOn = "2025-01-01", netAmount = new { amount = "1", currency = "EUR" },
            every = new { count = 1, unit = "month" }, version = 1,
        };
        var raise = new { label = "Salaire", effectiveOn = "2025-06-01", netAmount = new { amount = "1", currency = "EUR" } };

        async Task<List<(HttpStatusCode Status, string? Code)>> AnswersAsync(Guid source)
        {
            var answers = new[]
            {
                await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{source}/phases", Phase()),
                await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Sources}/{source}/phases/{pid}", correction),
                await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Sources}/{source}/phases/{pid}?version=1"),
                await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{source}/phase-changes", raise),
                await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{source}/expected?from=2025-01-01&to=2025-12-31"),
            };
            var list = new List<(HttpStatusCode, string?)>();
            foreach (var answer in answers)
                list.Add((answer.StatusCode, await CodeAsync(answer)));
            return list;
        }

        var hiddenAnswers = await AnswersAsync(hidden.Value);
        Assert.All(hiddenAnswers, a => Assert.Equal(HttpStatusCode.NotFound, a.Status));
        // Exactly like a source nobody uses: the answer gives nothing away.
        Assert.Equal(await AnswersAsync(unused), hiddenAnswers);

        var unchanged = Assert.Single(await StoredAsync(app, hidden.Value));
        Assert.Equal((Date("2025-01-01"), (LocalDate?)null, 1000m), (unchanged.StartsOn, unchanged.EndsOn, unchanged.Net));
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Income.Application;
using Nid.TestSupport;
using NodaTime.Text;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Income.IntegrationTests;

public class SourceEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Sources = "/api/v1/income/sources";

    private static object Phase(string label = "Salaire", string startsOn = "2025-01-01", string amount = "2300", string currency = "EUR",
        int count = 1, string unit = "month")
        => new { label, startsOn, netAmount = new { amount, currency }, every = new { count, unit } };

    private static Dictionary<string, object?> Create(NidApp app, string name = "Salaire", string visibility = "shared", params object[] phases)
        => new()
        {
            ["displayName"] = name, ["kind"] = "salary", ["currency"] = "EUR", ["visibility"] = visibility,
            ["payerLabel"] = "Employeur",
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

    [Fact]
    public async Task A_source_is_created_with_its_phases_and_read_back()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var id = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Sources, Create(app, visibility: "private", phases:
        [
            Phase("Salaire de base", "2025-01-01", "2300"),
            Phase("Prime annuelle", "2024-12-15", "1800", unit: "year"),
        ])));

        var list = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}?scope=me"));
        var row = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(id, row.GetProperty("id").GetGuid());
        Assert.Equal("2450.00", row.GetProperty("monthlyEquivalent").GetProperty("amount").GetString());
        Assert.Equal("EUR", row.GetProperty("monthlyEquivalent").GetProperty("currency").GetString());
        Assert.Equal(2, row.GetProperty("runningPhases").GetInt32());
        Assert.True(row.GetProperty("canWrite").GetBoolean());
        Assert.False(row.GetProperty("ended").GetBoolean());

        var detail = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{id}"));
        Assert.Equal("salary", detail.GetProperty("kind").GetString());
        Assert.Equal("Employeur", detail.GetProperty("payerLabel").GetString());
        Assert.Equal("private", detail.GetProperty("visibility").GetString());
        Assert.Equal("2450.00", detail.GetProperty("monthlyEquivalent").GetProperty("amount").GetString());

        await using var income = PostgresFixture.NewIncomeContext(app.ConnectionString);
        Assert.Equal(2, await income.Phases.CountAsync(p => p.SourceItemId == id));
    }

    [Fact]
    public async Task Visibility_is_required()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Create(app);
        body.Remove("visibility");

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, Sources, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await JsonAsync(refused);
        Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e => e.GetProperty("path").GetString() == "visibility");
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Equal(0, await core.Items.CountAsync(i => i.Module == "income"));
    }

    public static TheoryData<string, string, string> Rules => new()
    {
        { "kind", "income.unknown_kind", "kind" },
        { "payerLabel", "income.invalid_source", "payerLabel" },
        { "every", "income.unknown_cadence", "phases[0].every" },
        { "label", "income.invalid_phase", "phases[0].label" },
        { "netAmount", "core.amount_not_positive", "phases[0].netAmount" },
        { "currency", "core.currency_mismatch", "phases[0].netAmount" },
        { "overlap", "income.phase_overlap", "phases[1].startsOn" },
    };

    private static PhaseDraft Draft(string label = "Salaire", string startsOn = "2025-01-01", decimal amount = 2300m, Currency? currency = null,
        int count = 1, DurationUnit unit = DurationUnit.Month)
        => new(label, LocalDatePattern.Iso.Parse(startsOn).Value, null, Money.Create(amount, currency ?? Currency.Eur).Ok(),
            Duration.Create(count, unit).Ok(), null, null, null);

    /// <summary>The same refusal, as the pipeline gives it: the field it names is the failure's <c>field</c> metadata (as Investments').</summary>
    private static CreateIncomeSourceCommand BreakingCommand(NidApp app, string rule)
    {
        var command = new CreateIncomeSourceCommand("Salaire", "salary", Currency.Eur, Visibility.Shared,
            [new OwnerShare(app.AdultA, 10_000)], "Employeur", null, [Draft()]);
        return rule switch
        {
            "kind" => command with { Kind = "lottery" },
            "payerLabel" => command with { PayerLabel = new string('p', 101) },
            "every" => command with { Phases = [Draft(count: 2, unit: DurationUnit.Year)] },
            "label" => command with { Phases = [Draft(label: new string('l', 61))] },
            "netAmount" => command with { Phases = [Draft(amount: 0m)] },
            "currency" => command with { Phases = [Draft(currency: Currency.TryCreate("USD", out var usd) ? usd : throw new InvalidOperationException())] },
            "overlap" => command with { Phases = [Draft("Salaire", "2025-01-01"), Draft("Salaire", "2025-06-01", 2400m)] },
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null),
        };
    }

    private static Dictionary<string, object?> Breaking(NidApp app, string rule)
    {
        var body = Create(app, phases: [Phase()]);
        switch (rule)
        {
            case "kind": body["kind"] = "lottery"; break;
            case "payerLabel": body["payerLabel"] = new string('p', 101); break;
            case "every": body["phases"] = new[] { Phase(count: 2, unit: "year") }; break;
            case "label": body["phases"] = new[] { Phase(label: new string('l', 61)) }; break;
            case "netAmount": body["phases"] = new[] { Phase(amount: "0") }; break;
            case "currency": body["phases"] = new[] { Phase(currency: "USD") }; break;
            case "overlap": body["phases"] = new[] { Phase("Salaire", "2025-01-01"), Phase("Salaire", "2025-06-01", "2400") }; break;
            default: throw new ArgumentOutOfRangeException(nameof(rule), rule, null);
        }
        return body;
    }

    [Theory]
    [MemberData(nameof(Rules))]
    public async Task Every_rule_answers_at_its_field(string rule, string code, string field)
    {
        await using var app = await NidApp.StartAsync(fixture);

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, Sources, Breaking(app, rule));
        var (_, failure) = await app.InvokeAsync(app.AdultA, BreakingCommand(app, rule));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await JsonAsync(refused);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        // 2C2a spec §6: the field the rule names reaches the client, so a multi-line form places it on its line.
        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal(field, error.GetProperty("path").GetString());
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(code, failure?.Code);
        Assert.Equal(field, failure!.Metadata!["field"]);
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Equal(0, await core.Items.CountAsync(i => i.Module == "income"));
        await using var income = PostgresFixture.NewIncomeContext(app.ConnectionString);
        Assert.Equal(0, await income.Phases.CountAsync());
    }

    [Fact]
    public async Task Editing_sends_only_what_changed_and_clear_flags_empty()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Create(app);
        body["note"] = "Net après impôt";
        var id = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Sources, body));

        var edited = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Sources}/{id}", new { payerLabel = "Autre" });

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var detail = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{id}"));
        Assert.Equal("Autre", detail.GetProperty("payerLabel").GetString());
        Assert.Equal("Net après impôt", detail.GetProperty("note").GetString());
        Assert.Equal("salary", detail.GetProperty("kind").GetString());
        Assert.Equal("Salaire", detail.GetProperty("displayName").GetString());
        Assert.Equal("EUR", detail.GetProperty("currency").GetString());

        // A currency member is not part of the edit: it is ignored, and the source keeps EUR.
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Sources}/{id}", new { clearPayerLabel = true, currency = "USD" }))
            .EnsureSuccessStatusCode();

        detail = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{id}"));
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("payerLabel").ValueKind);
        Assert.Equal("Net après impôt", detail.GetProperty("note").GetString());
        Assert.Equal("EUR", detail.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Editing_checks_the_rules_and_renames()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Sources, Create(app)));

        var badKind = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Sources}/{id}", new { kind = "lottery" });
        var blankName = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Sources}/{id}", new { displayName = "  " });
        var renamed = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Sources}/{id}", new { displayName = "Salaire Acme", kind = "pension" });

        Assert.Equal("income.unknown_kind", (await JsonAsync(badKind)).GetProperty("code").GetString());
        Assert.Equal("income.invalid_source", (await JsonAsync(blankName)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var detail = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{id}"));
        Assert.Equal(("Salaire Acme", "pension"), (detail.GetProperty("displayName").GetString(), detail.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task Another_adults_private_source_and_non_source_items_are_not_found()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await app.NewIncomeSourceAsync("Secret de B", app.AdultB, visibility: "private");
        var envelope = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var subscription = await app.NewSubscriptionAsync("Spotify", app.AdultA, Visibility.Shared);

        var read = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{hidden.Value}");
        var unused = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        // Exactly like an id nobody uses: the answer gives nothing away.
        Assert.Equal((await JsonAsync(unused)).GetProperty("code").GetString(), (await JsonAsync(read)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Sources}/{hidden.Value}", new { note = "x" })).StatusCode);
        var list = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}?scope=me"));
        Assert.DoesNotContain(list.GetProperty("items").EnumerateArray(), s => s.GetProperty("id").GetGuid() == hidden.Value);

        // Every route of an income source answers 404 for an item that is not one, whatever its kind.
        foreach (var other in new[] { envelope, subscription })
        {
            var root = $"{Sources}/{other.Value}";
            var phase = new { label = "Salaire", startsOn = "2025-01-01", netAmount = new { amount = "100", currency = "EUR" }, every = new { count = 1, unit = "month" } };
            var correction = new { label = "Salaire", startsOn = "2025-01-01", netAmount = new { amount = "100", currency = "EUR" }, every = new { count = 1, unit = "month" }, version = 1 };
            var oneOff = new { occurredOn = "2025-06-10", amount = new { amount = "500", currency = "EUR" }, note = "Prime" };
            var routes = new (HttpMethod Method, string Url, object? Body)[]
            {
                (HttpMethod.Get, root, null),
                (HttpMethod.Patch, root, new { note = "x" }),
                (HttpMethod.Post, $"{root}/phases", phase),
                (HttpMethod.Put, $"{root}/phases/{Guid.NewGuid()}", correction),
                (HttpMethod.Delete, $"{root}/phases/{Guid.NewGuid()}?version=1", null),
                (HttpMethod.Post, $"{root}/phase-changes", new { label = "Salaire", effectiveOn = "2025-03-01", netAmount = new { amount = "100", currency = "EUR" } }),
                (HttpMethod.Get, $"{root}/expected?from=2025-01-01&to=2025-12-31", null),
                (HttpMethod.Post, $"{root}/one-offs", oneOff),
                (HttpMethod.Put, $"{root}/one-offs/{Guid.NewGuid()}", oneOff),
                (HttpMethod.Delete, $"{root}/one-offs/{Guid.NewGuid()}", null),
                (HttpMethod.Post, $"{root}/close", new { endedOn = "2025-09-30" }),
                (HttpMethod.Post, $"{root}/reopen", null),
            };
            foreach (var (method, url, body) in routes)
                Assert.True(HttpStatusCode.NotFound == (await app.RawAsync(app.AdultA, method, url, body)).StatusCode, $"{method} {url}");
        }
    }

    [Fact]
    public async Task Another_adult_reads_but_may_not_write_a_shared_source()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewIncomeSourceAsync("Salaire de A", app.AdultA);

        var refused = await app.RawAsync(app.AdultB, HttpMethod.Patch, $"{Sources}/{id.Value}", new { payerLabel = "Autre" });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var detail = await JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Sources}/{id.Value}"));
        Assert.False(detail.GetProperty("canWrite").GetBoolean());
    }

    [Fact]
    public async Task The_household_scope_hides_private_sources()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewIncomeSourceAsync("Partagé", app.AdultA);
        var mine = await app.NewIncomeSourceAsync("À moi", app.AdultA, visibility: "private");

        var household = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}?scope=household"));
        var me = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}?scope=me"));

        Assert.Equal([shared.Value], household.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("id").GetGuid()));
        Assert.Equal(new[] { mine.Value, shared.Value }.Order(),
            me.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).Order());
        // No phase yet: the monthly equivalent is zero, in the source's currency.
        Assert.Equal("0.00", me.GetProperty("items")[0].GetProperty("monthlyEquivalent").GetProperty("amount").GetString());
    }
}

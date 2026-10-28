using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.TaxEstimate;

/// <summary>Tax assessments spec §2 to §4, through the real API.</summary>
public class TaxAssessmentEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Route = "/api/v1/tax/assessments";

    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static object Declarant(PersonId person, int rank, object? extra = null)
        => extra is null ? new { personId = person.Value, rank } : Merge(new { personId = person.Value, rank }, extra);

    private static Dictionary<string, object?> Merge(object a, object b)
    {
        var map = new Dictionary<string, object?>();
        foreach (var o in new[] { a, b })
            if (o is Dictionary<string, object?> d)
                foreach (var (k, v) in d) map[k] = v;
            else
                foreach (var p in o.GetType().GetProperties())
                    map[p.Name[..1].ToLowerInvariant() + p.Name[1..]] = p.GetValue(o);
        return map;
    }

    private static Dictionary<string, object?> Body(int year, object[] declarants, object? extra = null)
        => Merge(new { year, declarants }, extra ?? new { });

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<(HttpStatusCode Status, string? Code, string? Path)> ProblemOf(HttpResponseMessage response)
    {
        var json = await JsonOf(response);
        var path = json.TryGetProperty("errors", out var errors) ? errors[0].GetProperty("path").GetString() : null;
        return (response.StatusCode, json.GetProperty("code").GetString(), path);
    }

    private static async Task<Guid> AddAsync(NidApp app, PersonId actor, int year, params object[] declarants)
    {
        var response = await app.RawAsync(actor, HttpMethod.Post, Route, Body(year, declarants));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonOf(response)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Adds_reads_and_lists_a_joint_notice()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Body(2023, [Declarant(app.AdultA, 1), Declarant(app.AdultB, 2, new { deduction = "actual", deductionAmount = Eur("4100.00") })],
            new { netTax = Eur("6412.00"), balance = Eur("-312.00"), averageRate = "14.2" });

        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, Route, body);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var view = await JsonOf(created);
        var id = view.GetProperty("id").GetGuid();
        Assert.Equal("-312.00", view.GetProperty("balance").GetProperty("amount").GetString());
        Assert.Equal("14.2", view.GetProperty("averageRate").GetString());
        Assert.Equal(2024, view.GetProperty("issuedYear").GetInt32());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("scaleTax").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("documentId").ValueKind);
        Assert.Equal("actual", view.GetProperty("declarants")[1].GetProperty("deduction").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("declarants")[0].GetProperty("deduction").ValueKind);

        var asA = await app.GetRawAsync(app.AdultA, $"{Route}/{id}");
        Assert.Equal(asA, await app.GetRawAsync(app.AdultB, $"{Route}/{id}"));
        foreach (var reader in new[] { app.AdultA, app.AdultB })
            Assert.Single((await JsonOf(await app.RawAsync(reader, HttpMethod.Get, $"{Route}?scope=household"))).GetProperty("items").EnumerateArray());
        Assert.Empty((await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Route}?year=2022"))).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_solo_assessment_is_absent_for_the_other_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await AddAsync(app, app.AdultA, 2018, Declarant(app.AdultA, 1));

        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Route}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Put, $"{Route}/{id}",
            Merge(Body(2018, [Declarant(app.AdultB, 1)]), new { version = 1u }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Route}/{id}")).StatusCode);
        foreach (var scope in new[] { "me", "household" })
            Assert.Empty((await JsonOf(await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Route}?scope={scope}"))).GetProperty("items").EnumerateArray());
        Assert.Empty((await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Route}?scope=household"))).GetProperty("items").EnumerateArray());
        Assert.Single((await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Route}?scope=me"))).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_solo_notice_makes_its_declarant_an_owner_of_private_items_and_a_joint_one_does_not()
    {
        await using var app = await NidApp.StartAsync(fixture);
        async Task<bool> OwnsAsync(PersonId who) => (await JsonOf(await app.RawAsync(who, HttpMethod.Get, "/api/v1/core/me/private-items"))).GetProperty("owns").GetBoolean();
        Assert.False(await OwnsAsync(app.AdultA));

        await AddAsync(app, app.AdultA, 2018, Declarant(app.AdultA, 1));
        await AddAsync(app, app.AdultA, 2019, Declarant(app.AdultA, 1), Declarant(app.AdultB, 2));

        Assert.True(await OwnsAsync(app.AdultA));
        Assert.False(await OwnsAsync(app.AdultB));
    }

    [Fact]
    public async Task Two_separate_notices_share_a_year_but_not_a_person()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await AddAsync(app, app.AdultA, 2018, Declarant(app.AdultA, 1));
        await AddAsync(app, app.AdultB, 2018, Declarant(app.AdultB, 1));

        var again = await app.RawAsync(app.AdultA, HttpMethod.Post, Route, Body(2018, [Declarant(app.AdultA, 1)]));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, TaxAssessmentCodes.DeclarantTaken, "declarants[0].personId"), await ProblemOf(again));
    }

    [Fact]
    public async Task A_correction_cannot_put_a_person_in_two_notices_of_a_year()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await AddAsync(app, app.AdultA, 2018, Declarant(app.AdultA, 1));
        var joint = await AddAsync(app, app.AdultA, 2019, Declarant(app.AdultA, 1), Declarant(app.AdultB, 2));
        var version = (await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Route}/{joint}"))).GetProperty("version").GetUInt32();

        var put = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Route}/{joint}",
            Merge(Body(2018, [Declarant(app.AdultA, 1), Declarant(app.AdultB, 2)]), new { version }));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, TaxAssessmentCodes.DeclarantTaken, "declarants[0].personId"), await ProblemOf(put));
    }

    [Fact]
    public async Task A_correction_must_keep_its_author_among_the_declarants()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var joint = await AddAsync(app, app.AdultA, 2023, Declarant(app.AdultA, 1), Declarant(app.AdultB, 2));
        var version = (await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Route}/{joint}"))).GetProperty("version").GetUInt32();

        var put = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Route}/{joint}", Merge(Body(2023, [Declarant(app.AdultB, 1)]), new { version }));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, TaxAssessmentCodes.DeclarantRequired, "declarants"), await ProblemOf(put));
    }

    [Fact]
    public async Task A_stale_version_is_a_conflict_and_a_missing_one_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await AddAsync(app, app.AdultA, 2023, Declarant(app.AdultA, 1));
        var version = (await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Route}/{id}"))).GetProperty("version").GetUInt32();
        var body = Merge(Body(2023, [Declarant(app.AdultA, 1)], new { netTax = Eur("100.00") }), new { version });

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Route}/{id}", body)).StatusCode);
        var stale = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Route}/{id}", body);
        var missing = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Route}/{id}", Body(2023, [Declarant(app.AdultA, 1)]));

        Assert.Equal((HttpStatusCode.Conflict, TaxAssessmentCodes.Conflict, null), await ProblemOf(stale));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "request.invalid", "version"), await ProblemOf(missing));
    }

    [Fact]
    public async Task The_store_refuses_a_stale_version_at_write_time()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await AddAsync(app, app.AdultA, 2023, Declarant(app.AdultA, 1));
        var assessment = new TaxAssessment(
            2023, 2024, [new TaxAssessmentDeclarant(app.AdultA, 1, null, null, null, null, null, null, null, null, null, null)],
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) with { NetTax = 100m };
        async Task<uint> VersionAsync()
        {
            await using var read = app.Services.CreateAsyncScope();
            return (await read.ServiceProvider.GetRequiredService<ITaxAssessmentStore>().FindAsync(id, default))!.Version;
        }
        async Task CorrectAsync(uint version)
        {
            await using var write = app.Services.CreateAsyncScope();
            write.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
            await write.ServiceProvider.GetRequiredService<ITaxAssessmentStore>().UpdateAsync(id, assessment, version, default);
        }
        var version = await VersionAsync();

        await CorrectAsync(version);

        await Assert.ThrowsAsync<TaxAssessmentChangedException>(() => CorrectAsync(version));
        Assert.NotEqual(version, await VersionAsync());
    }

    [Fact]
    public async Task The_store_maps_the_unique_index_to_taken_when_the_pre_check_is_raced()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var assessment = new TaxAssessment(
            2023, 2024, [new TaxAssessmentDeclarant(app.AdultA, 1, null, null, null, null, null, null, null, null, null, null)],
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
        async Task AddOnceAsync()
        {
            await using var write = app.Services.CreateAsyncScope();
            write.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
            await write.ServiceProvider.GetRequiredService<ITaxAssessmentStore>().AddAsync(assessment, default);
        }

        await AddOnceAsync();

        await Assert.ThrowsAsync<TaxAssessmentTakenException>(AddOnceAsync);
    }

    [Fact]
    public async Task A_pending_third_adult_neither_hides_a_joint_notice_nor_can_be_removed_while_declaring_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/admin/household", new { maxAdults = 3 })).EnsureSuccessStatusCode();
        var carol = (await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/admin/users",
            new { displayName = "Carol", email = "carol@example.test" }))).GetProperty("id").GetGuid();
        await AddAsync(app, app.AdultA, 2023, Declarant(app.AdultA, 1), Declarant(app.AdultB, 2));
        Assert.Single((await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Route}?scope=household"))).GetProperty("items").EnumerateArray());

        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            var row = new Nid.Core.Infrastructure.Persistence.TaxAssessmentRow { Id = Guid.NewGuid(), Year = 2010, Data = "{}" };
            core.TaxAssessments.Add(row);
            core.TaxAssessmentDeclarants.Add(new() { AssessmentId = row.Id, PersonId = carol, Year = 2010, Rank = 1 });
            await core.SaveChangesAsync();
        }
        var removed = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"/api/v1/admin/users/{carol}");

        Assert.Equal((HttpStatusCode.UnprocessableEntity, PersonCodes.PersonActive), (removed.StatusCode, (await ProblemOf(removed)).Code));
    }

    [Fact]
    public async Task The_unique_index_answers_as_a_field_error()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Body(2020, [Declarant(app.AdultA, 1)]);

        var responses = await Task.WhenAll(
            app.RawAsync(app.AdultA, HttpMethod.Post, Route, body), app.RawAsync(app.AdultA, HttpMethod.Post, Route, body));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var refused = Assert.Single(responses, r => r.StatusCode != HttpStatusCode.Created);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, TaxAssessmentCodes.DeclarantTaken, "declarants[0].personId"), await ProblemOf(refused));
    }

    [Fact]
    public async Task Delete_removes_it_and_it_is_audited_under_no_item()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await AddAsync(app, app.AdultA, 2023, Declarant(app.AdultA, 1));

        var deleted = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Route}/{id}");

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(id, (await JsonOf(deleted)).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Route}/{id}")).StatusCode);
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var rows = await platform.AuditLog.Where(r => r.Category == "write" && r.Entity == "core.tax_assessment").ToListAsync();
        Assert.Contains(rows, r => r.Action == "insert");
        Assert.Contains(rows, r => r.Action == "delete");
        Assert.All(rows, r => Assert.Empty(r.ItemIds));
        Assert.Contains(await platform.AuditLog.Where(r => r.Entity == "core.tax_assessment_declarant").ToListAsync(), r => r.Action == "delete");
    }

    [Fact]
    public async Task Validation_errors_name_their_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var usd = await app.RawAsync(app.AdultA, HttpMethod.Post, Route,
            Body(2023, [Declarant(app.AdultA, 1)], new { netTax = new { amount = "1.00", currency = "USD" } }));
        var future = await app.RawAsync(app.AdultA, HttpMethod.Post, Route, Body(2026, [Declarant(app.AdultA, 1)]));
        var deduction = await app.RawAsync(app.AdultA, HttpMethod.Post, Route, Body(2023, [Declarant(app.AdultA, 1, new { deduction = "flat" })]));

        Assert.Equal("netTax.currency", (await ProblemOf(usd)).Path);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, TaxAssessmentCodes.InvalidAssessment, "year"), await ProblemOf(future));
        Assert.Equal("declarants[0].deduction", (await ProblemOf(deduction)).Path);
    }
}

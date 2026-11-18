using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nid.TestSupport;
using Npgsql;

namespace Nid.Host.IntegrationTests.Host;

public class PersonRetirementEndpointTests(PostgresFixture fixture, SharedNidApp shared) : IClassFixture<PostgresFixture>, IClassFixture<SharedNidApp>
{
    private static string Url(Guid person) => $"/api/v1/core/persons/{person}/retirement";

    private static async Task<JsonElement> PersonAsync(NidApp app, Guid id)
        => (await app.GetAsync<JsonElement>(app.AdultA, "/api/v1/core/persons")).EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == id);

    [Fact]
    public async Task Adult_sets_another_persons_retirement()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, Url(app.AdultB.Value), new { birthDate = "1984-05-20", retirementAge = 62 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var person = await PersonAsync(app, app.AdultB.Value);
        Assert.Equal("1984-05-20", person.GetProperty("birthDate").GetString());
        Assert.Equal(62, person.GetProperty("retirementAge").GetInt32());
    }

    [Fact]
    public async Task A_change_to_the_person_saved_meanwhile_does_not_fail_the_retirement()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var ct = TestContext.Current.CancellationToken;
        // Another request (a guide marked seen, a theme) writes the same person row and holds it while the retirement is saved.
        await using var other = new NpgsqlConnection(app.ConnectionString);
        await other.OpenAsync(ct);
        await using var transaction = await other.BeginTransactionAsync(ct);
        await using (var touch = new NpgsqlCommand("update core.person set preferences = preferences where id = @id", other, transaction))
        {
            touch.Parameters.AddWithValue("id", app.AdultB.Value);
            await touch.ExecuteNonQueryAsync(ct);
        }

        var saving = app.RawAsync(app.AdultA, HttpMethod.Put, Url(app.AdultB.Value), new { birthDate = "1984-05-20", retirementAge = 62 });
        // The save reaches the row and waits on the other write's lock before that write commits.
        await using (var waiting = new NpgsqlConnection(app.ConnectionString))
        {
            await waiting.OpenAsync(ct);
            await using var blocked = new NpgsqlCommand("select count(*) from pg_locks where not granted", waiting);
            for (var tries = 0; (long)(await blocked.ExecuteScalarAsync(ct))! == 0; tries++)
            {
                Assert.True(tries < 200, "the retirement save never waited on the person row");
                await Task.Delay(50, ct);
            }
        }
        await transaction.CommitAsync(ct);

        Assert.Equal(HttpStatusCode.OK, (await saving).StatusCode);
        Assert.Equal(62, (await PersonAsync(app, app.AdultB.Value)).GetProperty("retirementAge").GetInt32());
    }

    [Fact]
    public async Task A_dependents_retirement_can_be_set_too()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, Url(app.Dependent.Value), new { birthDate = "2015-06-01", retirementAge = 67 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(67, (await PersonAsync(app, app.Dependent.Value)).GetProperty("retirementAge").GetInt32());
    }

    [Fact]
    public async Task Birth_date_in_the_future_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, Url(app.AdultB.Value), new { birthDate = "2030-01-01", retirementAge = 64 });

        await AssertRefusedAsync(response, "core.invalid_birth_date", "birthDate");
        Assert.Equal(JsonValueKind.Null, (await PersonAsync(app, app.AdultB.Value)).GetProperty("birthDate").ValueKind);
    }

    [Fact]
    public async Task Birth_date_before_1900_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, Url(app.AdultB.Value), new { birthDate = "1899-12-31", retirementAge = 64 });

        await AssertRefusedAsync(response, "core.invalid_birth_date", "birthDate");
    }

    [Theory]
    [InlineData(49)]
    [InlineData(76)]
    public async Task Age_outside_50_to_75_is_refused(int age)
    {
        var app = shared.App;

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, Url(app.AdultB.Value), new { birthDate = (string?)null, retirementAge = age });

        await AssertRefusedAsync(response, "core.invalid_retirement_age", "retirementAge");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(12)]
    public async Task Months_outside_0_to_11_are_refused(int months)
    {
        var app = shared.App;

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, Url(app.AdultB.Value), new { birthDate = (string?)null, retirementAge = 64, retirementMonths = months });

        await AssertRefusedAsync(response, "core.invalid_retirement_age", "retirementMonths");
    }

    [Fact]
    public async Task Age_and_months_out_of_range_are_both_reported()
    {
        var app = shared.App;

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, Url(app.AdultB.Value), new { birthDate = (string?)null, retirementAge = 80, retirementMonths = 12 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(["retirementAge", "retirementMonths"], problem.GetProperty("errors").EnumerateArray().Select(e => e.FieldPath()).ToArray());
    }

    [Fact]
    public async Task Unknown_person_is_404()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, Url(Guid.NewGuid()), new { birthDate = (string?)null, retirementAge = 64 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Null_birth_date_clears_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        (await app.RawAsync(app.AdultA, HttpMethod.Put, Url(app.AdultB.Value), new { birthDate = "1984-05-20", retirementAge = 62 })).EnsureSuccessStatusCode();

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, Url(app.AdultB.Value), new { birthDate = (string?)null, retirementAge = 62 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var person = await PersonAsync(app, app.AdultB.Value);
        Assert.Equal(JsonValueKind.Null, person.GetProperty("birthDate").ValueKind);
        Assert.Equal(62, person.GetProperty("retirementAge").GetInt32());
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string code, string field)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(field, problem.GetProperty("errors")[0].FieldPath());
    }
}

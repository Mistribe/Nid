using System.Text.Json;
using ModelContextProtocol.Client;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;

namespace Nid.TestSupport;

/// <summary>One item per module, through the real routes, with the bodies the module host tests use.</summary>
public static class McpSeed
{
    private static async Task<Guid> IdOf(HttpResponseMessage response)
    {
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    public static async Task<Guid> EnvelopeAsync(NidApp app, PersonId owner)
        => await IdOf(await app.RawAsync(owner, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = "PEA", kind = "pea", currency = "EUR", visibility = "shared",
            owners = new[] { new { personId = owner.Value, shareBp = 10_000 } },
            institution = "Banque", accountHint = "4521", valuationCadence = "monthly",
            opening = new { occurredOn = "2025-01-31", amount = new { amount = "20000.00", currency = "EUR" } },
            firstValuation = new { asOf = "2025-01-31", amount = new { amount = "21000.00", currency = "EUR" } },
        }));

    /// <summary>A shared envelope both adults own, so either may decide.</summary>
    public static async Task<Guid> JointEnvelopeAsync(NidApp app)
        => await IdOf(await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = "PEA commun", kind = "pea", currency = "EUR", visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 5_000 }, new { personId = app.AdultB.Value, shareBp = 5_000 } },
            institution = "Banque", valuationCadence = "monthly",
            opening = new { occurredOn = "2025-01-31", amount = new { amount = "20000.00", currency = "EUR" } },
            firstValuation = new { asOf = "2025-01-31", amount = new { amount = "21000.00", currency = "EUR" } },
        }));

    public static async Task<Guid> IncomeSourceAsync(NidApp app, PersonId owner, string visibility = "shared")
        => await IdOf(await app.RawAsync(owner, HttpMethod.Post, "/api/v1/income/sources", new Dictionary<string, object?>
        {
            ["displayName"] = "Salaire", ["kind"] = "salary", ["currency"] = "EUR", ["visibility"] = visibility,
            ["owners"] = new[] { new { personId = owner.Value, shareBp = 10_000 } },
            ["phases"] = new object[]
            {
                new { label = "Salaire", startsOn = "2025-01-01", endsOn = (string?)null,
                      netAmount = new { amount = "2300", currency = "EUR" }, every = new { count = 1, unit = "month" } },
            },
        }));

    public static Task<Guid> LoanAsync(NidApp app, PersonId owner)
        => LoanApi.NewAsync(app, LoanApi.Golden(app, "Prêt maison", "shared", owner), owner);

    public static Task<Guid> PropertyAsync(NidApp app, PersonId owner)
        => PropertyApi.NewPropertyAsync(app, PropertyApi.Home(app, "Maison", "shared", owner), owner);
}

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using NodaTime;

namespace Nid.TestSupport;

/// <summary>Shared by the loan endpoint tests: bodies and readers of <c>/api/v1/realestate/loans</c>.</summary>
public static class LoanApi
{
    public const string Loans = "/api/v1/realestate/loans";

    public static object Phase(
        int seq = 1, string mode = "annuity", string annualRate = "0.035", string rateType = "fixed", int? payments = null,
        string? paymentOverride = null)
        => new { seq, mode, annualRate, rateType, payments, paymentOverride };

    /// <summary>
    /// The contract members of loans.md §4's golden case (200 000 at 3.5 % over 240 payments), disbursed so that the host's
    /// today, 2025-10-15 (<see cref="NidApp.GoldenCaseToday"/>), is its 60th payment.
    /// </summary>
    public static Dictionary<string, object?> Contract(string principal = "200000", params object[] phases)
        => new()
        {
            ["principal"] = principal, ["disbursedOn"] = "2020-10-15", ["firstPaymentDate"] = "2020-11-15", ["totalPayments"] = 240,
            ["rounding"] = "half_up", ["firstPeriod"] = "full",
            ["phases"] = phases.Length == 0 ? new[] { Phase() } : phases, ["insurances"] = Array.Empty<object>(),
        };

    /// <summary>A creation body: the golden contract, named, in euros, borrowed by A unless <paramref name="borrowers"/> says otherwise.</summary>
    public static Dictionary<string, object?> Golden(NidApp app, string name = "Prêt maison", string visibility = "shared", params PersonId[] borrowers)
    {
        var owners = borrowers.Length == 0 ? [app.AdultA] : borrowers;
        var body = Contract();
        body["name"] = name;
        body["currency"] = "EUR";
        body["visibility"] = visibility;
        body["lender"] = "Banque";
        body["borrowers"] = owners.Select(p => new { personId = p.Value, shareBp = 10_000 / owners.Length }).ToArray();
        return body;
    }

    /// <summary>A short loan with no interest: 12 000 over 12 payments of 1 000 from 2025-01-15.</summary>
    public static Dictionary<string, object?> Short(NidApp app, string name = "Prêt travaux")
    {
        var body = Golden(app, name);
        body["principal"] = "12000";
        body["disbursedOn"] = "2025-01-01";
        body["firstPaymentDate"] = "2025-01-15";
        body["totalPayments"] = 12;
        body["phases"] = new[] { Phase(annualRate: "0") };
        return body;
    }

    /// <summary>
    /// The golden case's first three payments (loans.md §4, from 2020-11-15) as a French spreadsheet copies them: a header, semicolons,
    /// decimal commas, thousands after a non-breaking space.
    /// </summary>
    public const string BankTable = "Date;Échéance;Intérêts;Capital;Capital restant dû\n"
                                    + "15/11/2020;1159,92;583,33;576,59;199 423,41\n"
                                    + "15/12/2020;1159,92;581,65;578,27;198 845,14\n"
                                    + "15/01/2021;1159,92;579,96;579,96;198 265,18\n";

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public static async Task<Guid> CreatedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    public static async Task<Guid> NewAsync(NidApp app, Dictionary<string, object?> body, PersonId? actor = null)
        => await CreatedAsync(await app.RawAsync(actor ?? app.AdultA, HttpMethod.Post, Loans, body));

    public static async Task<JsonElement> DetailAsync(NidApp app, Guid id, PersonId? actor = null)
    {
        var response = await app.RawAsync(actor ?? app.AdultA, HttpMethod.Get, $"{Loans}/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    public static async Task<uint> ContractVersionAsync(NidApp app, Guid id)
        => (await DetailAsync(app, id)).GetProperty("contract").GetProperty("version").GetUInt32();

    /// <summary>A refusal: its status, and the code and path of its first located error (the top-level code when none).</summary>
    public static async Task<(HttpStatusCode Status, string? Code, string? Path)> RefusalAsync(HttpResponseMessage response)
    {
        var body = await JsonAsync(response);
        if (body.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
            return (response.StatusCode, errors[0].GetProperty("code").GetString(), errors[0].GetProperty("path").GetString());
        return (response.StatusCode, body.GetProperty("code").GetString(), null);
    }

    public static string? S(JsonElement element, string name) => element.GetProperty(name).GetString();

    public static JsonElement Only(JsonElement detail, string list) => Assert.Single(detail.GetProperty(list).EnumerateArray());

    /// <summary>The golden loan's 60th payment (the host's today) as a bank that owes 1 000 less: 161 253.12 left.</summary>
    public static readonly BankRow Sixtieth = new(new LocalDate(2025, 10, 15), 1_159.92m, 475.24m, 684.68m, null, 161_253.12m);

    /// <summary>Through the store (the routes come with Task 4): the table, then the reference.</summary>
    public static async Task StoreTableAsync(NidApp app, Guid loan, ScheduleSource source, params BankRow[] rows)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA); // audited rows need an actor (1F3a U4)
        var store = scope.ServiceProvider.GetRequiredService<ILoanStore>();
        await store.ReplaceBankScheduleAsync(new ItemId(loan), rows.Select(r => new StoredBankRow(Guid.CreateVersion7(), r)).ToList(), default);
        await store.UpdateDetailsAsync(new ItemId(loan), new LoanDetailsChange(Source: source), default);
    }
}

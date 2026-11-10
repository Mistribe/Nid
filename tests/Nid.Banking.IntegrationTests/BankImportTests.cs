using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Core.Contracts;
using Nid.Platform.Canonical;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Banking.IntegrationTests;

/// <summary>Bank sync D24: the sections <c>bank_accounts</c> and <c>bank_transactions</c> of <c>nid.data</c>, exported and imported through the real protocol.</summary>
public class BankImportTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Imports = "/api/v1/platform/imports";
    private const string Export = "/api/v1/platform/export";

    private static BankingDbContext Db(NidApp app) => new(new DbContextOptionsBuilder<BankingDbContext>().Configure(app.ConnectionString).Options);

    private static async Task AddAsync(NidApp app, Guid account, params NewBankTransaction[] lines)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<ITransactionStore>().AddAsync(account, lines, default);
    }

    private static NewBankTransaction Line(string reference, LocalDate on, decimal amount, string label)
        => new(reference, on, null, amount, Currency.Eur, label, "Contrepartie", null, "api");

    /// <summary>A's « Courant A » (IBAN) on a private envelope of hers with her card attached, three lines; B's private « Courant B » and its line.</summary>
    private static async Task<ItemId> SeedAsync(NidApp app)
    {
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", "FR7630006000011234567890189", "Courant A"), BankingApi.Account("uid-2", "hash-2", null, "Carte A", "CARD"));
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-3", "hash-3", null, "Courant B"));
        var ofA = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("id").GetGuid());
        var ofB = (await BankingApi.AccountsAsync(app, app.AdultB)).EnumerateArray().Single().GetProperty("id").GetGuid();
        var envelope = await app.NewEnvelopeAsync("Courant", app.AdultA, Visibility.Private);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{ofA["Courant A"]}", new { envelopeId = envelope.Value, autoApplyBalance = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{ofA["Carte A"]}", new { cardOfAccountId = ofA["Courant A"] })).StatusCode);
        await AddAsync(app, ofA["Courant A"], Line("e-1", new LocalDate(2025, 10, 1), -12.5m, "PRLV SEPA NETFLIX"), Line("e-2", new LocalDate(2025, 10, 2), 2450m, "VIR SALAIRE"));
        await AddAsync(app, ofA["Carte A"], Line("e-3", new LocalDate(2025, 10, 3), -30m, "CB BOULANGERIE"));
        await AddAsync(app, ofB, Line("e-4", new LocalDate(2025, 10, 4), -99m, "SECRET DE B"));
        return envelope;
    }

    private static async Task<List<JsonElement>> PageAsync(NidApp app, PersonId reader, string section)
    {
        var response = await app.RawAsync(reader, HttpMethod.Get, $"{Export}/{section}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return [.. (await BankingApi.JsonAsync(response)).GetProperty("entities").EnumerateArray()];
    }

    /// <summary>The reader's whole export, its non-empty sections as file JSON.</summary>
    internal static async Task<Dictionary<string, string>> ExportAsync(NidApp app, PersonId reader)
    {
        var manifest = await BankingApi.JsonAsync(await app.RawAsync(reader, HttpMethod.Get, $"{Export}/manifest"));
        var file = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var section in manifest.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("name").GetString()!))
            if (await PageAsync(app, reader, section) is { Count: > 0 } entities)
                file[section] = JsonSerializer.Serialize(entities);
        return file;
    }

    /// <summary>Starts a session as <paramref name="actor"/> (the file's <c>adult-1</c>), sends each section as batch 0, validates; commits when asked.</summary>
    internal static async Task<(JsonElement Report, List<JsonElement> BatchIssues)> ImportAsync(
        NidApp app, PersonId actor, IReadOnlyDictionary<string, string> sections, bool commit = true)
    {
        var started = await app.RawAsync(actor, HttpMethod.Post, Imports, new { format = "nid.data", version = 1, adults = new Dictionary<string, Guid> { ["adult-1"] = actor.Value } });
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var session = (await BankingApi.JsonAsync(started)).GetProperty("id").GetGuid();
        var batchIssues = new List<JsonElement>();
        foreach (var (section, entities) in sections)
        {
            using var parsed = JsonDocument.Parse(entities);
            var put = await app.RawTextAsync(actor, HttpMethod.Put, $"{Imports}/{session}/batches/{section}/0",
                $$"""{"hash":"{{Rfc8785.Hash(parsed.RootElement)}}","entities":{{entities}}}""");
            Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
            batchIssues.AddRange((await BankingApi.JsonAsync(put)).GetProperty("issues").EnumerateArray());
        }
        var validated = await app.RawAsync(actor, HttpMethod.Post, $"{Imports}/{session}/validate");
        Assert.True(validated.IsSuccessStatusCode, await validated.Content.ReadAsStringAsync());
        var report = (await BankingApi.JsonAsync(validated)).GetProperty("report");
        if (!commit || report.GetProperty("state").GetString() != "validated")
        {
            (await app.RawAsync(actor, HttpMethod.Delete, $"{Imports}/{session}")).EnsureSuccessStatusCode(); // one open session per person
            return (report, batchIssues);
        }
        var committed = await app.RawAsync(actor, HttpMethod.Post, $"{Imports}/{session}/commit");
        Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync());
        return ((await BankingApi.JsonAsync(committed)).GetProperty("report"), batchIssues);
    }

    private static IEnumerable<(string Code, string Path)> Errors(IEnumerable<JsonElement> issues)
        => issues.Where(i => i.GetProperty("severity").GetString() == "error").Select(i => (i.GetProperty("code").GetString()!, i.GetProperty("path").GetString()!));

    private static (int Created, int Unchanged) Counts(JsonElement report, string section)
    {
        var counts = report.GetProperty("sections").GetProperty(section);
        return (counts.GetProperty("created").GetInt32(), counts.GetProperty("unchanged").GetInt32());
    }

    [Fact]
    public async Task BankImport_export_lists_the_accounts_a_person_reads_and_their_lines_never_the_other_adults_private_ones()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await SeedAsync(app);

        var accounts = await PageAsync(app, app.AdultA, "bank_accounts");
        var lines = await PageAsync(app, app.AdultA, "bank_transactions");
        var envelopeRef = (await PageAsync(app, app.AdultA, "envelopes")).Single(e => e.GetProperty("name").GetString() == "Courant").GetProperty("id").GetString();

        Assert.Equal(["Carte A", "Courant A"], accounts.Select(a => a.GetProperty("name").GetString()));
        // Never a connection, a session, a source or a balance: the record holds these members only.
        Assert.All(accounts, a => Assert.Equal(
            ["ref", "name", "iban_last4", "kind", "currency", "envelope_ref", "card_of_ref", "auto_apply_balance", "ignored", "owner_ref"],
            a.EnumerateObject().Select(p => p.Name)));
        var (card, courant) = (accounts[0], accounts[1]);
        Assert.Equal(("0189", "checking", "EUR", envelopeRef, true, "adult-1"), (courant.GetProperty("iban_last4").GetString(), courant.GetProperty("kind").GetString(),
            courant.GetProperty("currency").GetString(), courant.GetProperty("envelope_ref").GetString(), courant.GetProperty("auto_apply_balance").GetBoolean(),
            courant.GetProperty("owner_ref").GetString()));
        Assert.Equal(("card", courant.GetProperty("ref").GetString()), (card.GetProperty("kind").GetString(), card.GetProperty("card_of_ref").GetString()));
        Assert.Equal(JsonValueKind.Null, card.GetProperty("envelope_ref").ValueKind);

        // By account (by name: the card first), then date.
        Assert.Equal(["CB BOULANGERIE", "PRLV SEPA NETFLIX", "VIR SALAIRE"], lines.Select(l => l.GetProperty("label").GetString()));
        var first = lines[1];
        Assert.Equal(
            ["account_ref", "provider_ref", "fingerprint", "booked_on", "value_on", "amount", "currency", "label", "counterparty", "remittance", "fetched_via"],
            first.EnumerateObject().Select(p => p.Name));
        Assert.Equal((courant.GetProperty("ref").GetString(), "e-1", "2025-10-01", "-12.5000", "EUR", "Contrepartie", "api"),
            (first.GetProperty("account_ref").GetString(), first.GetProperty("provider_ref").GetString(), first.GetProperty("booked_on").GetString(),
             first.GetProperty("amount").GetString(), first.GetProperty("currency").GetString(), first.GetProperty("counterparty").GetString(),
             first.GetProperty("fetched_via").GetString()));
        Assert.Matches("^[0-9a-f]{64}$", first.GetProperty("fingerprint").GetString());

        // B's export: her own account only; A's private ones and their lines never.
        Assert.Equal(["Courant B"], (await PageAsync(app, app.AdultB, "bank_accounts")).Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(["SECRET DE B"], (await PageAsync(app, app.AdultB, "bank_transactions")).Select(l => l.GetProperty("label").GetString()));
        Assert.DoesNotContain(lines, l => l.GetProperty("label").GetString() == "SECRET DE B");
    }

    [Fact]
    public async Task BankImport_into_an_empty_household_recreates_the_accounts_under_the_importers_file_connection_and_twice_adds_nothing()
    {
        await using var source = await BankingApi.StartAsync(fixture);
        await SeedAsync(source);
        var file = await ExportAsync(source, source.AdultA);

        await using var target = await BankingApi.StartAsync(fixture);
        var (report, _) = await ImportAsync(target, target.AdultA, file);

        Assert.Equal("committed", report.GetProperty("state").GetString());
        Assert.Equal((2, 0), Counts(report, "bank_accounts"));
        Assert.Equal((3, 0), Counts(report, "bank_transactions"));
        var envelope = (await PageAsync(target, target.AdultA, "envelopes")).Single(e => e.GetProperty("name").GetString() == "Courant").GetProperty("id").GetString();
        var accounts = (await BankingApi.AccountsAsync(target, target.AdultA)).EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!);
        Assert.Equal(["Carte A", "Courant A"], accounts.Keys.Order());
        var courant = accounts["Courant A"];
        Assert.Equal(("0189", "checking", true), (courant.GetProperty("ibanMasked").GetString(), courant.GetProperty("kind").GetString(), courant.GetProperty("autoApplyBalance").GetBoolean()));
        Assert.Equal(courant.GetProperty("id").GetGuid(), accounts["Carte A"].GetProperty("cardOfAccountId").GetGuid());
        Assert.Equal("card", accounts["Carte A"].GetProperty("kind").GetString());
        // Re-associated by ref: the account sits on the envelope this import created from the file's.
        Assert.Equal(envelope, (await PageAsync(target, target.AdultA, "bank_accounts")).Single(a => a.GetProperty("name").GetString() == "Courant A").GetProperty("envelope_ref").GetString());
        await using (var db = Db(target))
        {
            var fileConnection = await db.Connections.SingleAsync(c => c.Source == "file", cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(target.AdultA.Value, fileConnection.PersonId);
            Assert.Equal(2, await db.Sources.CountAsync(s => s.ConnectionId == fileConnection.Id, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.Connections.CountAsync(cancellationToken: TestContext.Current.CancellationToken)); // no bank connection, no session
            Assert.Equal(3, await db.Transactions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(0, await db.Balances.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        }

        var (again, _) = await ImportAsync(target, target.AdultA, file);

        Assert.Equal("committed", again.GetProperty("state").GetString());
        Assert.Equal((0, 2), Counts(again, "bank_accounts"));
        Assert.Equal((0, 3), Counts(again, "bank_transactions"));
        await using (var db = Db(target))
        {
            Assert.Equal(2, await db.Accounts.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(3, await db.Transactions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task BankImport_of_a_persons_own_export_finds_her_accounts_by_ref_and_adds_nothing()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await SeedAsync(app);
        var export = await ExportAsync(app, app.AdultA);
        var file = new Dictionary<string, string>
        {
            ["persons"] = export["persons"], ["bank_accounts"] = export["bank_accounts"], ["bank_transactions"] = export["bank_transactions"],
        };

        var (report, _) = await ImportAsync(app, app.AdultA, file);

        Assert.Equal("committed", report.GetProperty("state").GetString());
        Assert.Equal((0, 2), Counts(report, "bank_accounts"));
        Assert.Equal((0, 3), Counts(report, "bank_transactions"));
        await using var db = Db(app);
        Assert.Equal(3, await db.Accounts.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await db.Connections.AnyAsync(c => c.Source == "file", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BankImport_refuses_an_exported_ref_of_an_account_the_importer_cannot_change_without_naming_it()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Courant A"), BankingApi.Account("uid-2", "hash-2", null, "Livret A", "SVGS"));
        var ofA = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("id").GetGuid());
        // « Courant A » on A's private envelope (B cannot read it), « Livret A » on A's shared one (B reads it, only A writes it).
        foreach (var (name, visibility) in new[] { ("Courant A", Visibility.Private), ("Livret A", Visibility.Shared) })
        {
            var envelope = await app.NewEnvelopeAsync(name, app.AdultA, visibility);
            Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{ofA[name]}", new { envelopeId = envelope.Value })).StatusCode);
        }
        await AddAsync(app, ofA["Courant A"], Line("e-1", new LocalDate(2025, 10, 1), -12.5m, "PRLV SEPA NETFLIX"));
        await AddAsync(app, ofA["Livret A"], Line("e-2", new LocalDate(2025, 10, 2), 100m, "VIR EPARGNE"));
        var export = await ExportAsync(app, app.AdultA);
        // B's file: A's two accounts, each with one new line.
        var file = new Dictionary<string, string>
        {
            ["persons"] = export["persons"],
            ["bank_accounts"] = export["bank_accounts"],
            ["bank_transactions"] = JsonSerializer.Serialize((await PageAsync(app, app.AdultA, "bank_accounts")).Select((a, i) => new Dictionary<string, object>
            {
                ["account_ref"] = a.GetProperty("ref").GetString()!, ["provider_ref"] = $"b-{i}", ["fingerprint"] = "x", ["booked_on"] = "2025-10-05",
                ["amount"] = "-1.00", ["currency"] = "EUR", ["label"] = "LIGNE DE B", ["fetched_via"] = "file",
            })),
        };

        var (report, _) = await ImportAsync(app, app.AdultB, file);

        Assert.Equal("invalid", report.GetProperty("state").GetString());
        var refused = report.GetProperty("issues").EnumerateArray().Where(i => i.GetProperty("code").GetString() == BankingCodes.FileAccountMismatch).ToList();
        Assert.Equal(["bank_accounts[0].ref", "bank_accounts[1].ref"], refused.Select(i => i.GetProperty("path").GetString()));
        Assert.All(refused, i =>
        {
            var message = i.GetProperty("message").GetString()!;
            Assert.DoesNotContain("Courant", message);
            Assert.DoesNotContain("Livret", message);
            Assert.DoesNotContain(ofA["Courant A"].ToString(), message);
            Assert.DoesNotContain(ofA["Livret A"].ToString(), message);
        });
        // Their lines are not reported again, and none reached A's accounts.
        Assert.DoesNotContain(report.GetProperty("issues").EnumerateArray(), i => i.GetProperty("path").GetString()!.StartsWith("bank_transactions", StringComparison.Ordinal));
        await using var db = Db(app);
        Assert.Equal(1, await db.Transactions.CountAsync(t => t.AccountId == ofA["Courant A"], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.Transactions.CountAsync(t => t.AccountId == ofA["Livret A"], cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await db.Connections.AnyAsync(c => c.Source == "file", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BankImport_hand_written_refs_are_each_importers_own()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        static Dictionary<string, string> File(string name) => new()
        {
            ["persons"] = """[{"ref":"adult-1","kind":"adult","display_name":"Moi"}]""",
            ["bank_accounts"] = $$"""[{"ref":"courant","name":"{{name}}","kind":"checking","currency":"EUR","owner_ref":"adult-1"}]""",
            ["bank_transactions"] = """[{"account_ref":"courant","provider_ref":"r-1","fingerprint":"x","booked_on":"2025-10-01","amount":"-5","currency":"EUR","label":"CAFE","fetched_via":"file"}]""",
        };

        var (ofA, _) = await ImportAsync(app, app.AdultA, File("Courant de A"));
        // B's « courant » is not A's private one: no refusal that would tell her it exists, an account of her own.
        var (ofB, _) = await ImportAsync(app, app.AdultB, File("Courant de B"));

        Assert.Equal(((1, 0), (1, 0)), (Counts(ofA, "bank_accounts"), Counts(ofB, "bank_accounts")));
        Assert.Equal(["Courant de B"], (await BankingApi.AccountsAsync(app, app.AdultB)).EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        Assert.Equal((0, 1), Counts((await ImportAsync(app, app.AdultA, File("Courant de A"))).Report, "bank_transactions"));
    }

    [Fact]
    public async Task BankImport_refuses_an_envelope_of_another_currency_and_an_unknown_kind()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        const string owner = """[{"person":"adult-1","share_bp":10000}]""";
        var file = new Dictionary<string, string>
        {
            ["persons"] = """[{"ref":"adult-1","kind":"adult","display_name":"A"}]""",
            ["envelopes"] = $$"""[{"id":"env-usd","name":"Dollars","kind":"checking","currency":"USD","ownership":{{owner}}}]""",
            ["bank_accounts"] = """
                [{"ref":"acc-eur","name":"Courant","iban_last4":null,"kind":"checking","currency":"EUR","envelope_ref":"env-usd","card_of_ref":null,"auto_apply_balance":false,"ignored":false,"owner_ref":"adult-1"}]
                """,
        };

        var (report, _) = await ImportAsync(app, app.AdultA, file, commit: false);

        Assert.Equal("invalid", report.GetProperty("state").GetString());
        Assert.Contains((BankingCodes.CurrencyMismatch, "bank_accounts[0].envelope_ref"), Errors(report.GetProperty("issues").EnumerateArray()));

        // A kind the format does not know is refused on receipt.
        file["bank_accounts"] = """[{"ref":"acc-odd","name":"Prêt","kind":"loan","currency":"EUR","owner_ref":"adult-1"}]""";
        var (_, batchIssues) = await ImportAsync(app, app.AdultA, file, commit: false);

        Assert.Contains((ImportCodes.InvalidValue, "bank_accounts[0].kind"), Errors(batchIssues));
    }

    [Fact]
    public async Task BankImport_refuses_an_envelope_the_importer_cannot_change()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        const string owner = """[{"person":"adult-1","share_bp":10000}]""";
        // A's shared envelope, imported by A: B reads it, only A writes it.
        var envelopes = $$"""[{"id":"env-a","name":"Courant de A","kind":"checking","currency":"EUR","visibility":"shared","ownership":{{owner}}}]""";
        var (ofA, _) = await ImportAsync(app, app.AdultA, new Dictionary<string, string>
        {
            ["persons"] = """[{"ref":"adult-1","kind":"adult","display_name":"A"}]""", ["envelopes"] = envelopes,
        });
        Assert.Equal("committed", ofA.GetProperty("state").GetString());

        var (report, _) = await ImportAsync(app, app.AdultB, new Dictionary<string, string>
        {
            ["persons"] = """[{"ref":"adult-1","kind":"adult","display_name":"A"}]""",
            ["envelopes"] = envelopes,
            ["bank_accounts"] = """[{"ref":"courant","name":"Courant de B","kind":"checking","currency":"EUR","envelope_ref":"env-a","owner_ref":"adult-1"}]""",
            ["bank_transactions"] = """[{"account_ref":"courant","provider_ref":"r-1","fingerprint":"x","booked_on":"2025-10-01","amount":"-5","currency":"EUR","label":"CAFE","fetched_via":"file"}]""",
        }, commit: false);

        Assert.Equal("invalid", report.GetProperty("state").GetString());
        Assert.Equal([(ImportCodes.NotPermitted, "bank_accounts[0].envelope_ref")], Errors(report.GetProperty("issues").EnumerateArray()));
        await using var db = Db(app);
        Assert.Equal((0, 0), (await db.Accounts.CountAsync(cancellationToken: TestContext.Current.CancellationToken), await db.Transactions.CountAsync(cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("9999-12-31")]
    [InlineData("1899-12-31")]
    public async Task BankImport_refuses_a_line_booked_out_of_range(string bookedOn)
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var file = new Dictionary<string, string>
        {
            ["persons"] = """[{"ref":"adult-1","kind":"adult","display_name":"A"}]""",
            ["bank_accounts"] = """[{"ref":"courant","name":"Courant","kind":"checking","currency":"EUR","owner_ref":"adult-1"}]""",
            ["bank_transactions"] = $$"""[{"account_ref":"courant","provider_ref":"r-1","fingerprint":"x","booked_on":"{{bookedOn}}","amount":"-5","currency":"EUR","label":"CAFE","fetched_via":"api"}]""",
        };

        var (report, batchIssues) = await ImportAsync(app, app.AdultA, file, commit: false);

        // Before 1900 the batch is refused on receipt; past next week, at validation (it needs today).
        Assert.Contains((ImportCodes.InvalidValue, "bank_transactions[0].booked_on"), Errors([.. batchIssues, .. report.GetProperty("issues").EnumerateArray()]));
        await using var db = Db(app);
        Assert.Equal(0, await db.Transactions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>A's « Courant » on a shared envelope A owns, B too when <paramref name="sharedWithB"/>, two lines, and A's subscription « Netflix » of the visibility given.</summary>
    private static async Task<(Guid Account, ItemId Subscription, Guid Line)> LinkedSeedAsync(NidApp app, Visibility visibility, bool sharedWithB)
    {
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant"));
        var account = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray().Single().GetProperty("id").GetGuid();
        var envelope = sharedWithB
            ? await app.NewEnvelopeAsync("Commun", Visibility.Shared, (app.AdultA, 5000), (app.AdultB, 5000))
            : await app.NewEnvelopeAsync("Commun", app.AdultA, Visibility.Shared);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{account}", new { envelopeId = envelope.Value })).StatusCode);
        var subscription = await app.NewSubscriptionAsync("Netflix", app.AdultA, visibility);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{subscription.Value}/phases", new
        {
            startsOn = "2025-01-05", price = new { amount = "12.99", currency = "EUR" }, every = new { count = 1, unit = "month" }, payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        await AddAsync(app, account, Line("n-1", new LocalDate(2025, 10, 5), -12.99m, "PRLV SEPA NETFLIX 123"), Line("n-2", new LocalDate(2025, 10, 6), -4m, "CB CAFE"));
        await using var scope = app.Services.CreateAsyncScope();
        var line = (await scope.ServiceProvider.GetRequiredService<ITransactionStore>().ListAsync([account], null, null, 0, 10, default)).Single(t => t.ProviderRef == "n-1").Id;
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/transactions/{line}/link", new { module = "subscriptions", itemId = subscription.Value })).EnsureSuccessStatusCode();
        return (account, subscription, line);
    }

    /// <summary>Childcare decision 26: a line linked to a childminder's statement round-trips with its arrangement.</summary>
    [Fact]
    public async Task A_link_to_a_childcare_arrangement_round_trips()
    {
        await using var source = await BankingApi.StartAsync(fixture);
        var created = await source.RawAsync(source.AdultA, HttpMethod.Post, "/api/v1/childcare", new
        {
            name = "Garde de Léa", childId = source.Dependent.Value, carer = new { name = "Martine", pajemploi = (string?)null, approval = (string?)null },
            terms = new
            {
                from = "2025-09-01", netHourlyRate = "4", grossHourlyRate = (string?)null, overtimePct = "10", upkeepPer9h = "4", lunchPrice = (string?)null,
                weekTypes = new[] { new { name = "Semaine type", weeks = 46, schedule = new[] { new { day = 1, arrival = "08:00", departure = "17:30", lunch = false } } } },
            },
            gaps = Array.Empty<object>(), visibility = "shared", owners = new[] { new { personId = source.AdultA.Value, shareBp = 10_000 } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var arrangement = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("id").GetGuid();
        await BankingApi.ConnectAsync(source, source.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant"));
        var account = (await BankingApi.AccountsAsync(source, source.AdultA)).EnumerateArray().Single().GetProperty("id").GetGuid();
        var envelope = await source.NewEnvelopeAsync("Courant", source.AdultA, Visibility.Shared);
        Assert.Equal(HttpStatusCode.OK, (await source.RawAsync(source.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{account}", new { envelopeId = envelope.Value })).StatusCode);
        await AddAsync(source, account, Line("p-1", new LocalDate(2025, 10, 6), -80m, "PRLV SEPA URSSAF PAJEMPLOI"));
        Guid line;
        await using (var scope = source.Services.CreateAsyncScope())
            line = (await scope.ServiceProvider.GetRequiredService<ITransactionStore>().ListAsync([account], null, null, 0, 10, TestContext.Current.CancellationToken)).Single().Id;
        Assert.Equal(HttpStatusCode.OK, (await source.RawAsync(source.AdultA, HttpMethod.Post, $"{BankingApi.Root}/transactions/{line}/link",
            new { module = "childcare", itemId = arrangement })).StatusCode);
        var file = await ExportAsync(source, source.AdultA);
        var arrangementRef = JsonDocument.Parse(file["childcare_arrangements"]).RootElement.EnumerateArray().Single().GetProperty("id").GetString();
        Assert.Equal(arrangementRef, JsonDocument.Parse(file["bank_links"]).RootElement.EnumerateArray().Single().GetProperty("target_ref").GetString());

        await using var target = await BankingApi.StartAsync(fixture);
        var (report, _) = await ImportAsync(target, target.AdultA, file);

        Assert.True(report.GetProperty("state").GetString() == "committed", report.GetProperty("issues").ToString());
        Assert.Equal((1, 0), Counts(report, "bank_links"));
        var linked = JsonDocument.Parse(await target.GetRawAsync(target.AdultA, $"{BankingApi.Root}/transactions?state=linked")).RootElement.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(("childcare", "Garde de Léa"), (linked.GetProperty("link").GetProperty("module").GetString(), linked.GetProperty("link").GetProperty("itemName").GetString()));
    }

    [Fact]
    public async Task Links_and_rules_round_trip()
    {
        await using var source = await BankingApi.StartAsync(fixture);
        await LinkedSeedAsync(source, Visibility.Shared, false);
        var file = await ExportAsync(source, source.AdultA);

        var link = JsonDocument.Parse(file["bank_links"]).RootElement.EnumerateArray().Single();
        var subscriptionRef = (await PageAsync(source, source.AdultA, "subscriptions")).Single().GetProperty("id").GetString();
        Assert.Equal(("n-1", "as_expected", subscriptionRef, JsonValueKind.Null), (link.GetProperty("provider_ref").GetString(), link.GetProperty("outcome").GetString(),
            link.GetProperty("target_ref").GetString(), link.GetProperty("counterpart_ref").ValueKind));
        var rule = JsonDocument.Parse(file["bank_rules"]).RootElement.EnumerateArray().Single();
        Assert.Equal((subscriptionRef, "NETFLIX"), (rule.GetProperty("target_ref").GetString(), rule.GetProperty("label_stem").GetString()));

        await using var target = await BankingApi.StartAsync(fixture);
        var (report, _) = await ImportAsync(target, target.AdultA, file);

        Assert.True(report.GetProperty("state").GetString() == "committed", report.GetProperty("issues").ToString());
        Assert.Equal((1, 0), Counts(report, "bank_links"));
        Assert.Equal((1, 0), Counts(report, "bank_rules"));
        var linked = JsonDocument.Parse(await target.GetRawAsync(target.AdultA, $"{BankingApi.Root}/transactions?state=linked")).RootElement.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(("PRLV SEPA NETFLIX 123", "Netflix", "as_expected"), (linked.GetProperty("label").GetString(), linked.GetProperty("link").GetProperty("itemName").GetString(),
            linked.GetProperty("link").GetProperty("outcome").GetString()));
        Assert.Equal(["CB CAFE"], JsonDocument.Parse(await target.GetRawAsync(target.AdultA, $"{BankingApi.Root}/transactions")).RootElement.GetProperty("items")
            .EnumerateArray().Select(l => l.GetProperty("label").GetString()));
        await using (var db = Db(target))
            Assert.Equal($"person:{target.AdultA.Value:D}", (await db.Links.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).DecidedBy);
        Assert.Single(JsonDocument.Parse(await target.GetRawAsync(target.AdultA, $"{BankingApi.Root}/rules")).RootElement.EnumerateArray());

        var (again, _) = await ImportAsync(target, target.AdultA, file);

        Assert.Equal((0, 1), Counts(again, "bank_links"));
        Assert.Equal((0, 1), Counts(again, "bank_rules"));
    }

    [Fact]
    public async Task A_link_to_an_unreadable_target_is_not_exported()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await LinkedSeedAsync(app, Visibility.Private, true);

        // B reads the shared account and its lines, not A's private subscription it pays: no link and no rule of it.
        Assert.Equal(2, (await PageAsync(app, app.AdultB, "bank_transactions")).Count);
        Assert.Empty(await PageAsync(app, app.AdultB, "bank_links"));
        Assert.Empty(await PageAsync(app, app.AdultB, "bank_rules"));
        Assert.Single(await PageAsync(app, app.AdultA, "bank_links"));
    }

    private const string Persons = """[{"ref":"adult-1","kind":"adult","display_name":"Moi"}]""";
    private const string Subscription = """{"id":"dup","name":"Netflix","ownership":[{"person":"adult-1","share_bp":10000}],"visibility":"shared","auto_renew":true}""";
    private const string Courant = """[{"ref":"courant","name":"Courant","kind":"checking","currency":"EUR","owner_ref":"adult-1"}]""";
    private const string Lines = """[{"account_ref":"courant","provider_ref":"r-1","fingerprint":"x","booked_on":"2025-10-01","amount":"-12.99","currency":"EUR","label":"NETFLIX","fetched_via":"file"}]""";

    private static Dictionary<string, string> LinkFile(string subscriptions, string link, string? properties = null)
    {
        var file = new Dictionary<string, string>
        {
            ["persons"] = Persons, ["subscriptions"] = subscriptions, ["bank_accounts"] = Courant, ["bank_transactions"] = Lines, ["bank_links"] = link,
        };
        if (properties is not null)
            file["properties"] = properties;
        return file;
    }

    [Fact]
    public async Task A_link_to_an_id_two_sections_share_is_refused()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var property = """{"id":"dup","name":"Maison","property_type":"primary_residence","currency":"EUR","purchase_date":"2020-06-15","purchase_price":{"amount":"250000.00","currency":"EUR"},"ownership":[{"person":"adult-1","share_bp":10000}],"visibility":"shared"}""";
        var link = """[{"account_ref":"courant","provider_ref":"r-1","outcome":"as_expected","target_ref":"dup","decided_at":"2025-10-02T10:00:00Z"}]""";

        var (report, batchIssues) = await ImportAsync(app, app.AdultA, LinkFile($"[{Subscription}]", link, $"[{property}]"));

        Assert.Contains((ImportCodes.UnresolvedReference, "bank_links[0].target_ref"), Errors([.. batchIssues, .. report.GetProperty("issues").EnumerateArray()]));
    }

    [Fact]
    public async Task A_rents_flow_key_is_not_carried_to_another_instance()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var link = """[{"account_ref":"courant","provider_ref":"r-1","outcome":"income","target_ref":"dup","target_key":"flow:0123456789abcdef0123456789abcdef","decided_at":"2025-10-02T10:00:00Z"}]""";

        var (report, _) = await ImportAsync(app, app.AdultA, LinkFile($"[{Subscription}]", link));

        Assert.Equal("committed", report.GetProperty("state").GetString());
        await using var db = Db(app);
        var stored = await db.Links.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(("income", null), (stored.Outcome, stored.TargetKey));
    }
}

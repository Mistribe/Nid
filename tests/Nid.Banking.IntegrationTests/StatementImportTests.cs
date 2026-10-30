using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Application.Sync;
using Nid.Banking.Contracts;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.IntegrationTests;

/// <summary>Bank sync spec §5 and D23: a statement file previewed, then confirmed; its account found again or created, its closing balance valued.</summary>
public class StatementImportTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Iban = "FR7630006000011234567890189";
    private const string OtherIban = "FR7610107001011234567890129";
    private const string Statements = BankingApi.Root + "/statements";

    private sealed class SettableClock(Instant now) : IClock
    {
        public Instant Now { get; set; } = now;

        public Instant GetCurrentInstant() => Now;
    }

    private sealed record Line(string FitId, LocalDate On, decimal Amount, string Name);

    private static LocalDate D(int month, int day) => new(2026, month, day);

    private static Instant At(LocalDate day) => day.At(new LocalTime(10, 0)).InUtc().ToInstant();

    private static BankingDbContext Db(NidApp app) => new(new DbContextOptionsBuilder<BankingDbContext>().Configure(app.ConnectionString).Options);

    private static async Task<(NidApp App, SettableClock Clock)> StartAsync(PostgresFixture fixture, LocalDate today)
    {
        var clock = new SettableClock(At(today));
        return (await BankingApi.StartAsync(fixture, overrides: s => s.AddSingleton<IClock>(clock)), clock);
    }

    private static readonly Line[] October =
    [
        new("F1", D(10, 12), -13.49m, "CARTE X1234 11/10 AMAZON EU SARL"),
        new("F2", D(10, 15), -84.20m, "PRLV SEPA EDF CLIENTS PARTICULIE"),
        new("F3", D(10, 27), 2450m, "VIR SEPA SOCIETE EXEMPLE SAS"),
    ];

    /// <summary>An OFX 2 statement of <paramref name="account"/> (an IBAN), its closing balance <paramref name="closing"/> on <paramref name="closingOn"/>.</summary>
    private static byte[] Ofx(string account, IReadOnlyList<Line> lines, decimal closing, LocalDate closingOn, string currency = "EUR")
    {
        static string Day(LocalDate d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var entries = string.Concat(lines.Select(l =>
            $"<STMTTRN><TRNTYPE>OTHER</TRNTYPE><DTPOSTED>{Day(l.On)}</DTPOSTED><TRNAMT>{l.Amount.ToString(CultureInfo.InvariantCulture)}</TRNAMT>"
            + $"<FITID>{l.FitId}</FITID><NAME>{l.Name}</NAME></STMTTRN>"));
        return Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <?OFX OFXHEADER="200" VERSION="211" SECURITY="NONE" OLDFILEUID="NONE" NEWFILEUID="NONE"?>
            <OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS>
            <CURDEF>{currency}</CURDEF>
            <BANKACCTFROM><ACCTID>{account}</ACCTID><ACCTTYPE>CHECKING</ACCTTYPE></BANKACCTFROM>
            <BANKTRANLIST><DTSTART>{Day(lines.Min(l => l.On))}</DTSTART><DTEND>{Day(lines.Max(l => l.On))}</DTEND>{entries}</BANKTRANLIST>
            <LEDGERBAL><BALAMT>{closing.ToString(CultureInfo.InvariantCulture)}</BALAMT><DTASOF>{Day(closingOn)}</DTASOF></LEDGERBAL>
            </STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
            """);
    }

    private static byte[] October_() => Ofx(Iban, October, 1234.56m, D(10, 28));

    private static Task<HttpResponseMessage> ImportAsync(
        NidApp app, PersonId person, byte[] file, bool confirm, ItemId? envelope = null, bool keepProbablyKnown = false)
        => app.RawContentAsync(person, HttpMethod.Post, Statements, DocumentApi.Multipart(file,
            $"fileName=releve.ofx&confirm={confirm}&keepProbablyKnown={keepProbablyKnown}" + (envelope is { } e ? $"&envelopeId={e.Value}" : "")));

    private static async Task<JsonElement> ImportedAsync(
        NidApp app, PersonId person, byte[] file, bool confirm, ItemId? envelope = null, bool keepProbablyKnown = false)
    {
        var response = await ImportAsync(app, person, file, confirm, envelope, keepProbablyKnown);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await BankingApi.JsonAsync(response);
    }

    private static (int New, int Known, int Probably, bool Written) Counts(JsonElement view)
        => (view.GetProperty("newCount").GetInt32(), view.GetProperty("knownCount").GetInt32(), view.GetProperty("probablyKnownCount").GetInt32(),
            view.GetProperty("written").GetBoolean());

    private static async Task AssertNothingWrittenAsync(NidApp app, int accounts = 0)
    {
        await using var db = Db(app);
        Assert.Equal((accounts, 0, 0, 0), (await db.Accounts.CountAsync(), await db.Transactions.CountAsync(), await db.Balances.CountAsync(),
            await db.Connections.CountAsync(c => c.Source == "file")));
    }

    private static async Task AssociateAsync(NidApp app, PersonId person, Guid account, ItemId envelope, bool auto = false)
        => Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(person, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{account}",
            new { envelopeId = envelope.Value, autoApplyBalance = auto })).StatusCode);

    [Fact]
    public async Task A_preview_of_a_new_account_reads_the_file_and_writes_nothing()
    {
        var (app, _) = await StartAsync(fixture, D(10, 29));
        await using var _app = app;

        var view = await ImportedAsync(app, app.AdultA, October_(), confirm: false);

        Assert.Equal(("ofx", JsonValueKind.Null, true, "Relevé ·0189", "0189"),
            (view.GetProperty("format").GetString(), view.GetProperty("accountId").ValueKind, view.GetProperty("newAccount").GetBoolean(),
                view.GetProperty("accountName").GetString(), view.GetProperty("ibanMasked").GetString()));
        Assert.Equal(("2026-10-12", "2026-10-27"), (view.GetProperty("from").GetString(), view.GetProperty("to").GetString()));
        Assert.Equal((3, 0, 0, false), Counts(view));
        Assert.Equal(("1234.56", "2026-10-28", "EUR"),
            (view.GetProperty("closingBalance").GetString(), view.GetProperty("closingBalanceOn").GetString(), view.GetProperty("currency").GetString()));
        await AssertNothingWrittenAsync(app);
    }

    [Fact]
    public async Task Confirming_creates_the_account_under_the_person_s_file_connection()
    {
        var (app, clock) = await StartAsync(fixture, D(10, 29));
        await using var _app = app;

        var view = await ImportedAsync(app, app.AdultA, October_(), confirm: true);

        Assert.Equal((3, 0, 0, true), Counts(view));
        await using var db = Db(app);
        var connection = await db.Connections.SingleAsync();
        Assert.Equal(("file", app.AdultA.Value, "active", true), (connection.Source, connection.PersonId, connection.Status, connection.SessionId is null));
        var account = await db.Accounts.SingleAsync();
        Assert.Equal((view.GetProperty("accountId").GetGuid(), app.AdultA.Value, "checking", "EUR", "Relevé ·0189", "0189"),
            (account.Id, account.CreatedByPersonId, account.Kind, account.Currency, account.Name, account.IbanMasked));
        Assert.Equal(connection.Id, (await db.Sources.SingleAsync(s => s.AccountId == account.Id)).ConnectionId);
        var stored = await db.Transactions.OrderBy(t => t.BookedOn).ToListAsync();
        Assert.Equal([("F1", -13.49m, "file"), ("F2", -84.20m, "file"), ("F3", 2450m, "file")], stored.Select(t => (t.ProviderRef, t.Amount, t.FetchedVia)));
        var balance = await db.Balances.SingleAsync();
        Assert.Equal((D(10, 28), 1234.56m), (balance.AsOf, balance.Amount));
        // A's account, read like any other; the night leaves the file connection alone (it has no session to read).
        Assert.Equal("Relevé ·0189", Assert.Single((await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray()).GetProperty("name").GetString());
        Assert.Empty((await BankingApi.AccountsAsync(app, app.AdultB)).EnumerateArray());
        clock.Now = At(D(10, 30));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            Assert.True((await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new SyncAllCommand())).IsSuccess);
        }
        await using var after = Db(app);
        Assert.Equal("active", (await after.Connections.SingleAsync()).Status);
    }

    [Fact]
    public async Task Re_importing_the_same_file_adds_nothing()
    {
        var (app, _) = await StartAsync(fixture, D(10, 29));
        await using var _app = app;
        var first = await ImportedAsync(app, app.AdultA, October_(), confirm: true);

        var again = await ImportedAsync(app, app.AdultA, October_(), confirm: false);

        Assert.Equal((false, first.GetProperty("accountId").GetGuid()), (again.GetProperty("newAccount").GetBoolean(), again.GetProperty("accountId").GetGuid()));
        Assert.Equal((0, 3, 0, false), Counts(again));
        Assert.Equal((0, 3, 0, true), Counts(await ImportedAsync(app, app.AdultA, October_(), confirm: true)));
        await using var db = Db(app);
        Assert.Equal((1, 3, 1), (await db.Accounts.CountAsync(), await db.Transactions.CountAsync(), await db.Connections.CountAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_closing_balance_values_the_envelope_s_month_end(bool auto)
    {
        var (app, clock) = await StartAsync(fixture, D(8, 12));
        await using var _app = app;
        var envelope = await app.NewEnvelopeAsync("Courant", app.AdultA, Visibility.Private);
        var account = (await ImportedAsync(app, app.AdultA, Ofx(Iban, [new("A1", D(8, 3), -5m, "CB BOULANGERIE")], 900m, D(8, 10)), confirm: true))
            .GetProperty("accountId").GetGuid();
        await AssociateAsync(app, app.AdultA, account, envelope, auto);
        clock.Now = At(D(9, 5));

        var view = await ImportedAsync(app, app.AdultA, Ofx(Iban, [new("A2", D(8, 30), -20m, "CB PHARMACIE")], 1000m, D(8, 31)), confirm: true, envelope);

        Assert.Equal((1, 0, 0, true), Counts(view));
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var proposals = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, ProposalKindHelpers.Proposals)).RootElement.GetProperty("items")
            .EnumerateArray().Where(p => p.GetProperty("sourceLabel").GetString() == "Banque").ToList();
        if (auto)
        {
            Assert.Empty(proposals);
            var value = await core.Valuations.SingleAsync(v => v.ItemId == envelope.Value);
            Assert.Equal((D(8, 31), 1000m, "Solde bancaire du 31/08/2026", "bank_api"), (value.AsOf, value.Amount, value.Note, value.Source));
        }
        else
        {
            var proposal = Assert.Single(proposals);
            Assert.Equal(("2026-08-31", "Solde bancaire du 31/08/2026"),
                (proposal.GetProperty("subjectKey").GetString(), proposal.GetProperty("payload").GetProperty("note").GetString()));
            Assert.False(await core.Valuations.AnyAsync(v => v.ItemId == envelope.Value));
        }
        await using var db = Db(app);
        Assert.Equal(D(8, 31), (await db.Accounts.SingleAsync()).LastValuedOn);
    }

    [Fact]
    public async Task An_ofx_after_the_api_leaves_probable_duplicates_out()
    {
        var (app, clock) = await StartAsync(fixture, D(10, 29));
        await using var _app = app;
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", Iban, "Courant A"));
        app.Banks.Balances[("session-code-a", "uid-1")] = [new ProviderBalance("CLBD", 1234.56m, "EUR", D(10, 28))];
        app.Banks.Transactions[("session-code-a", "uid-1")] =
        [
            new("api-1", D(10, 12), null, -13.49m, "EUR", "CARTE X1234 11/10 AMAZON EU SARL", null, null),
            new("api-2", D(10, 15), null, -84.20m, "EUR", "PRLV SEPA EDF CLIENTS PARTICULIERS ECHEANCE OCTOBRE", null, null),
            new("api-3", D(10, 26), null, 2450m, "EUR", "VIR SEPA SOCIETE EXEMPLE SAS SALAIRE OCTOBRE", null, null),
        ];
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new SyncAllCommand());
        }
        // The same booking with the label cut (F2), and a day apart (F3): probably known; F1 is the same line; F4 is new.
        var file = Ofx(Iban, [.. October, new("F4", D(10, 28), -84.20m, "PRLV SEPA EDF CLIENTS PARTICULIE")], 1234.56m, D(10, 28));

        var preview = await ImportedAsync(app, app.AdultA, file, confirm: false);
        Assert.False(preview.GetProperty("newAccount").GetBoolean());
        Assert.Equal((1, 1, 2, false), Counts(preview));

        Assert.Equal((1, 1, 2, true), Counts(await ImportedAsync(app, app.AdultA, file, confirm: true)));
        await using (var db = Db(app))
            Assert.Equal(["F4"], await db.Transactions.Where(t => t.FetchedVia == "file").Select(t => t.ProviderRef).ToListAsync());

        Assert.Equal((2, 2, 2, true), Counts(await ImportedAsync(app, app.AdultA, file, confirm: true, keepProbablyKnown: true)));
        await using (var db = Db(app))
            Assert.Equal(["F2", "F3", "F4"], await db.Transactions.Where(t => t.FetchedVia == "file").OrderBy(t => t.ProviderRef).Select(t => t.ProviderRef).ToListAsync());
    }

    [Fact]
    public async Task A_file_of_another_account_is_refused_on_an_envelope()
    {
        var (app, _) = await StartAsync(fixture, D(10, 29));
        await using var _app = app;
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", OtherIban, "Courant A"));
        var envelope = await app.NewEnvelopeAsync("Courant", app.AdultA, Visibility.Private);
        await AssociateAsync(app, app.AdultA, Assert.Single((await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray()).GetProperty("id").GetGuid(), envelope);

        foreach (var confirm in new[] { false, true })
            await BankingApi.AssertRefusedAsync(await ImportAsync(app, app.AdultA, October_(), confirm, envelope),
                HttpStatusCode.UnprocessableEntity, BankingCodes.FileAccountMismatch);

        await AssertNothingWrittenAsync(app, accounts: 1);
    }

    [Fact]
    public async Task Dropped_on_an_envelope_with_no_account_the_new_account_is_associated()
    {
        var (app, _) = await StartAsync(fixture, D(10, 29));
        await using var _app = app;
        var envelope = await app.NewEnvelopeAsync("Courant", app.AdultA, Visibility.Private);
        // Its balances would become the envelope's values: a dollar statement goes with no euro envelope.
        await BankingApi.AssertRefusedAsync(await ImportAsync(app, app.AdultA, Ofx(Iban, October, 10m, D(10, 28), "USD"), confirm: true, envelope),
            HttpStatusCode.UnprocessableEntity, BankingCodes.CurrencyMismatch);
        await AssertNothingWrittenAsync(app);
        // B does not write A's private envelope, nor sees it.
        Assert.Equal(HttpStatusCode.NotFound, (await ImportAsync(app, app.AdultB, October_(), confirm: true, envelope)).StatusCode);
        await AssertNothingWrittenAsync(app);

        var view = await ImportedAsync(app, app.AdultA, October_(), confirm: true, envelope);

        var account = Assert.Single((await BankingApi.AccountsAsync(app, app.AdultA, envelope)).EnumerateArray());
        Assert.Equal((view.GetProperty("accountId").GetGuid(), envelope.Value), (account.GetProperty("id").GetGuid(), account.GetProperty("envelopeId").GetGuid()));
        await using var db = Db(app);
        Assert.Equal(app.AdultA.Value, (await db.Accounts.SingleAsync()).AssociatedByPersonId);
    }

    [Fact]
    public async Task A_file_of_an_account_the_reader_cannot_see_is_refused_without_naming_it()
    {
        var (app, _) = await StartAsync(fixture, D(10, 29));
        await using var _app = app;
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-b", "hash-b", Iban, "Compte secret de B"));
        await using var before = Db(app);
        var ofB = await before.Accounts.SingleAsync();

        foreach (var confirm in new[] { false, true })
        {
            var response = await ImportAsync(app, app.AdultA, October_(), confirm);
            var body = await response.Content.ReadAsStringAsync();
            await BankingApi.AssertRefusedAsync(response, HttpStatusCode.UnprocessableEntity, BankingCodes.FileAccountMismatch);
            foreach (var secret in new[] { "Compte secret", ofB.Id.ToString(), "0189" })
                Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        }

        await using var db = Db(app);
        Assert.Equal((1, 0, 0, 1), (await db.Accounts.CountAsync(), await db.Transactions.CountAsync(), await db.Connections.CountAsync(c => c.Source == "file"),
            await db.Sources.CountAsync()));
    }

    [Fact]
    public async Task A_file_over_5_mib_is_refused()
    {
        var (app, _) = await StartAsync(fixture, D(10, 29));
        await using var _app = app;
        var file = new byte[5 * 1024 * 1024 + 1];
        file.AsSpan().Fill((byte)' ');

        await BankingApi.AssertRefusedAsync(await ImportAsync(app, app.AdultA, file, confirm: true), HttpStatusCode.RequestEntityTooLarge, BankingCodes.FileTooLarge);
        await AssertNothingWrittenAsync(app);
    }

    [Fact]
    public async Task A_malformed_file_is_refused_with_its_line()
    {
        var (app, _) = await StartAsync(fixture, D(10, 29));
        await using var _app = app;
        var file = Encoding.UTF8.GetString(October_()).Replace("<TRNAMT>-84.20</TRNAMT>", "<TRNAMT>beaucoup</TRNAMT>");

        var response = await ImportAsync(app, app.AdultA, Encoding.UTF8.GetBytes(file), confirm: true);

        await BankingApi.AssertRefusedAsync(response, HttpStatusCode.UnprocessableEntity, BankingCodes.FileMalformed);
        Assert.Equal(6, (await BankingApi.JsonAsync(response)).GetProperty("line").GetInt32());
        await BankingApi.AssertRefusedAsync(await ImportAsync(app, app.AdultA, "Date;Montant\n"u8.ToArray(), confirm: false),
            HttpStatusCode.UnprocessableEntity, BankingCodes.FileFormat);
        await AssertNothingWrittenAsync(app);
    }

    [Fact]
    public async Task File_imports_need_banking_on_only()
    {
        // Banking on, but neither an Enable Banking application id nor a private key.
        await using var app = await NidApp.StartAsync(fixture, s => s.AddSingleton<IClock>(new SettableClock(At(D(10, 29)))));
        await BankingApi.SettingsAsync(app, new { bankingEnabled = true });
        var envelope = await app.NewEnvelopeAsync("Courant", app.AdultA, Visibility.Private);

        Assert.Equal((3, 0, 0, true), Counts(await ImportedAsync(app, app.AdultA, October_(), confirm: true, envelope)));

        Assert.Equal(envelope.Value, Assert.Single((await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray()).GetProperty("envelopeId").GetGuid());
        // Reaching a bank still needs them.
        await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{BankingApi.Root}/aspsps"),
            HttpStatusCode.Conflict, BankingCodes.NotConfigured);
    }

    /// <summary>B imports A's statement before A connects the bank: the bank connection outranks the file, the account becomes A's.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(Visibility.Private)]
    [InlineData(Visibility.Shared)]
    public async Task A_bank_connection_outranks_a_file_claim(Visibility? envelopeOfB)
    {
        var (app, _) = await StartAsync(fixture, D(10, 29));
        await using var _app = app;
        var envelope = envelopeOfB is { } visibility ? await app.NewEnvelopeAsync("Courant de B", app.AdultB, visibility) : (ItemId?)null;
        var account = (await ImportedAsync(app, app.AdultB, October_(), confirm: true, envelope)).GetProperty("accountId").GetGuid();

        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", Iban, "Courant A"));

        await using var db = Db(app);
        var row = await db.Accounts.SingleAsync();
        Assert.Equal((account, app.AdultA.Value), (row.Id, row.CreatedByPersonId));
        var ofA = Assert.Single((await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray());
        // Written by whoever writes its envelope (5A), else by its owner.
        Assert.Equal(envelopeOfB != Visibility.Shared, ofA.GetProperty("canWrite").GetBoolean());
        if (envelopeOfB == Visibility.Shared)
        {
            // A reads B's shared envelope: the account stays on it, and B reads it there.
            Assert.Equal(envelope!.Value.Value, row.EnvelopeItemId);
            Assert.Single((await BankingApi.AccountsAsync(app, app.AdultB)).EnumerateArray());
        }
        else
        {
            // B's private envelope would hide A's own account from A: it goes, and B no longer reads A's bank.
            Assert.Equal((null, null, null), (row.EnvelopeItemId, row.AssociatedOn, row.AssociatedByPersonId));
            Assert.Empty((await BankingApi.AccountsAsync(app, app.AdultB)).EnumerateArray());
        }
    }

    /// <summary>A card follows its parent's access: the claim takes B's account to A, and B's own card must not go with it.</summary>
    [Fact]
    public async Task A_bank_connection_claiming_an_account_detaches_the_other_adult_s_card()
    {
        var (app, _) = await StartAsync(fixture, D(10, 29));
        await using var _app = app;
        var account = (await ImportedAsync(app, app.AdultB, October_(), confirm: true)).GetProperty("accountId").GetGuid();
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-c", "hash-c", null, "Carte B", "CARD"));
        var card = Assert.Single((await BankingApi.AccountsAsync(app, app.AdultB)).EnumerateArray(), a => a.GetProperty("name").GetString() == "Carte B").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{card}", new { cardOfAccountId = account })).StatusCode);

        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", Iban, "Courant A"));

        await using var db = Db(app);
        Assert.Equal((app.AdultA.Value, null), ((await db.Accounts.SingleAsync(a => a.Id == account)).CreatedByPersonId, (await db.Accounts.SingleAsync(a => a.Id == card)).CardOfAccountId));
        var ofB = Assert.Single((await BankingApi.AccountsAsync(app, app.AdultB)).EnumerateArray());
        Assert.Equal(card, ofB.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Two_confirms_of_a_first_file_at_once_write_it_once()
    {
        var (app, _) = await StartAsync(fixture, D(10, 29));
        await using var _app = app;

        var responses = await Task.WhenAll(ImportAsync(app, app.AdultA, October_(), confirm: true), ImportAsync(app, app.AdultA, October_(), confirm: true));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        await using var db = Db(app);
        Assert.Equal((1, 1, 3), (await db.Connections.CountAsync(), await db.Accounts.CountAsync(), await db.Transactions.CountAsync()));
    }

    [Fact]
    public async Task A_line_booked_more_than_a_week_ahead_is_refused_with_its_line()
    {
        var (app, _) = await StartAsync(fixture, D(10, 29));
        await using var _app = app;
        // A deferred card's upcoming entries, or a typo: it would move every later read of the bank past the real lines.
        var file = Ofx(Iban, [.. October, new("F9", D(11, 6), -1m, "CB A VENIR")], 1234.56m, D(10, 28));

        var response = await ImportAsync(app, app.AdultA, file, confirm: false);

        await BankingApi.AssertRefusedAsync(response, HttpStatusCode.UnprocessableEntity, BankingCodes.FileMalformed);
        Assert.Equal(6, (await BankingApi.JsonAsync(response)).GetProperty("line").GetInt32());
        // A week ahead is still a booking.
        Assert.Equal((4, 0, 0, true), Counts(await ImportedAsync(app, app.AdultA, Ofx(Iban, [.. October, new("F9", D(11, 5), -1m, "CB A VENIR")], 1234.56m, D(10, 28)), confirm: true)));
    }

    [Fact]
    public async Task D23_holds_back_one_file_line_per_api_line()
    {
        var (app, _) = await StartAsync(fixture, D(10, 29));
        await using var _app = app;
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", Iban, "Courant A"));
        app.Banks.Balances[("session-code-a", "uid-1")] = [new ProviderBalance("CLBD", 10m, "EUR", D(10, 28))];
        app.Banks.Transactions[("session-code-a", "uid-1")] = [new("api-1", D(10, 20), null, -3.50m, "EUR", "CB CAFE DU COIN 75011 PARIS", null, null)];
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new SyncAllCommand());
        }
        // Two coffees: the API's one is the file's on the same day; the next day's is another.
        var file = Ofx(Iban, [new("F1", D(10, 20), -3.50m, "CAFE DU COIN"), new("F2", D(10, 21), -3.50m, "CAFE DU COIN")], 10m, D(10, 28));

        Assert.Equal((1, 0, 1, true), Counts(await ImportedAsync(app, app.AdultA, file, confirm: true)));

        await using var db = Db(app);
        Assert.Equal(["F2"], await db.Transactions.Where(t => t.FetchedVia == "file").Select(t => t.ProviderRef).ToListAsync());
    }

    [Fact]
    public async Task A_credit_card_statement_makes_a_card_account_that_takes_no_envelope()
    {
        var (app, _) = await StartAsync(fixture, D(10, 29));
        await using var _app = app;
        var card = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(October_())
            .Replace("<STMTRS>", "<CCSTMTRS>").Replace("</STMTRS>", "</CCSTMTRS>").Replace("BANKACCTFROM>", "CCACCTFROM>").Replace("<ACCTTYPE>CHECKING</ACCTTYPE>", ""));
        var envelope = await app.NewEnvelopeAsync("Courant", app.AdultA, Visibility.Private);

        await BankingApi.AssertRefusedAsync(await ImportAsync(app, app.AdultA, card, confirm: true, envelope), HttpStatusCode.UnprocessableEntity, BankingCodes.EnvelopeKind);
        await AssertNothingWrittenAsync(app);

        Assert.Equal((3, 0, 0, true), Counts(await ImportedAsync(app, app.AdultA, card, confirm: true)));
        await using var db = Db(app);
        Assert.Equal(("card", null), ((await db.Accounts.SingleAsync()).Kind, (await db.Accounts.SingleAsync()).EnvelopeItemId));
    }

    [Fact]
    public async Task A_confirmed_import_is_audited_with_counts_only_and_a_preview_is_not()
    {
        var (app, _) = await StartAsync(fixture, D(10, 29));
        await using var _app = app;
        var envelope = await app.NewEnvelopeAsync("Courant", app.AdultA, Visibility.Private);
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);

        await ImportedAsync(app, app.AdultA, October_(), confirm: false, envelope);
        Assert.False(await platform.AuditLog.AnyAsync(r => r.Action == SecurityEventNames.BankStatementImported));

        var account = (await ImportedAsync(app, app.AdultA, October_(), confirm: true, envelope)).GetProperty("accountId").GetGuid();

        var entry = await platform.AuditLog.SingleAsync(r => r.Action == SecurityEventNames.BankStatementImported);
        Assert.Equal(("security", app.AdultA.Value, app.AdultA.Value, envelope.Value), (entry.Category, entry.PersonId, entry.SubjectPersonId, Assert.Single(entry.ItemIds)));
        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(entry.Data)!;
        Assert.Equal(new Dictionary<string, string>
        {
            ["account"] = account.ToString(), ["source"] = "file", ["new"] = "3", ["known"] = "0", ["probably_known"] = "0", ["kept"] = "false",
        }, data);
        foreach (var secret in new[] { "AMAZON", "EDF", "13.49", "1234.56", "0189", "FR76" })
            Assert.DoesNotContain(secret, entry.Data, StringComparison.Ordinal);
        // The importer reads it among their own events.
        var mine = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/platform/audit/me/security")).RootElement.GetProperty("entries");
        Assert.Contains(mine.EnumerateArray(), e => e.GetProperty("event").GetString() == SecurityEventNames.BankStatementImported
            && e.GetProperty("details").GetProperty("new").GetString() == "3");
    }

    [Fact]
    public async Task A_claimed_account_drops_the_other_adult_s_balance_settings()
    {
        var (app, _) = await StartAsync(fixture, D(10, 29));
        await using var _app = app;
        var envelope = await app.NewEnvelopeAsync("Courant de B", app.AdultB, Visibility.Shared);
        var account = (await ImportedAsync(app, app.AdultB, October_(), confirm: true, envelope)).GetProperty("accountId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{account}",
            new { autoApplyBalance = true, ignored = true })).StatusCode);

        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", Iban, "Courant A"));

        await using var db = Db(app);
        var row = await db.Accounts.SingleAsync();
        Assert.Equal((app.AdultA.Value, false, false), (row.CreatedByPersonId, row.AutoApplyBalance, row.Ignored));
    }

    /// <summary>Finds no account from a file, as when another person's import commits between the import's lookup and its creation.</summary>
    private sealed class LateStore(AccountStore inner) : IAccountStore
    {
        public Task<BankAccount?> FindFromFileAsync(string? iban, string accountId, CancellationToken ct) => Task.FromResult<BankAccount?>(null);

        public Task<IReadOnlyList<BankAccount>> UpsertFromSessionAsync(Guid connection, IReadOnlyList<SessionAccount> accounts, CancellationToken ct)
            => inner.UpsertFromSessionAsync(connection, accounts, ct);

        public Task<BankAccount?> FindAsync(Guid id, CancellationToken ct) => inner.FindAsync(id, ct);

        public Task<BankAccount> UpsertFromFileAsync(
            Guid fileConnection, string? iban, string accountId, string currency, string name, CancellationToken ct,
            BankAccountKind kind = BankAccountKind.Checking, string? ibanLast4 = null)
            => inner.UpsertFromFileAsync(fileConnection, iban, accountId, currency, name, ct, kind, ibanLast4);

        public Task<bool> ClaimFromFileAsync(Guid account, PersonId person, CancellationToken ct) => inner.ClaimFromFileAsync(account, person, ct);

        public Task<BankAccount?> ForEnvelopeAsync(ItemId envelope, CancellationToken ct) => inner.ForEnvelopeAsync(envelope, ct);

        public Task<IReadOnlyList<BankAccount>> ForConnectionAsync(Guid connection, CancellationToken ct) => inner.ForConnectionAsync(connection, ct);

        public Task<IReadOnlyList<BankAccount>> AllAsync(CancellationToken ct) => inner.AllAsync(ct);

        public Task<IReadOnlyList<BankAccountSource>> SourcesAsync(Guid account, CancellationToken ct) => inner.SourcesAsync(account, ct);

        public Task<UnambitiousFx.Functional.Result> UpdateAsync(BankAccount account, CancellationToken ct) => inner.UpdateAsync(account, ct);

        public Task RecordValuedAsync(Guid account, LocalDate valuedOn, LocalDate? conflictOn, CancellationToken ct) => inner.RecordValuedAsync(account, valuedOn, conflictOn, ct);
    }

    [Fact]
    public async Task An_account_another_person_created_meanwhile_is_refused()
    {
        await using var app = await BankingApi.StartAsync(fixture,
            overrides: s => s.AddScoped<IAccountStore>(sp => new LateStore(ActivatorUtilities.CreateInstance<AccountStore>(sp)))
                .AddSingleton<IClock>(new SettableClock(At(D(10, 29)))));
        await ImportedAsync(app, app.AdultB, October_(), confirm: true);

        await BankingApi.AssertRefusedAsync(await ImportAsync(app, app.AdultA, October_(), confirm: true),
            HttpStatusCode.UnprocessableEntity, BankingCodes.FileAccountMismatch);

        await using var db = Db(app);
        Assert.Equal((1, app.AdultB.Value, 3), (await db.Accounts.CountAsync(), (await db.Accounts.SingleAsync()).CreatedByPersonId, await db.Transactions.CountAsync()));
    }
}

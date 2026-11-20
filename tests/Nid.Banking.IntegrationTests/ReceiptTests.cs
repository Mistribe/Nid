using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Application.Sync;
using Nid.Banking.Contracts;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Core.Contracts;
using Nid.Documents.Application;
using Nid.Documents.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.TestSupport.DocumentApi;

namespace Nid.Banking.IntegrationTests;

/// <summary>#84: a receipt on a bank line, read by whoever reads the account's envelope, and following the account when it moves.</summary>
public class ReceiptTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Kind = "banking.transaction";
    private const string Iban = "FR7630006000011234567890189";

    private static readonly Instant At = new LocalDate(2026, 10, 29).At(new LocalTime(10, 0)).InUtc().ToInstant();

    private sealed class FixedClock : IClock
    {
        public Instant GetCurrentInstant() => At;
    }

    private sealed record Setup(NidApp App, Guid Courant, Guid Carte, Guid Livret, ItemId Joint, Guid Connection) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>A's « Courant A » and its card on a shared envelope of A's (B reads it), and A's « Livret A » on no envelope.</summary>
    private static async Task<Setup> StartAsync(PostgresFixture fixture)
    {
        var app = await BankingApi.StartAsync(fixture, overrides: s => s.AddSingleton<IClock>(new FixedClock()));
        var connection = await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"),
            BankingApi.Account("uid-2", "hash-2", null, "Carte A", "CARD"), BankingApi.Account("uid-3", "hash-3", Iban, "Livret A", "SVGS"));
        var accounts = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray()
            .ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("id").GetGuid());
        var joint = await app.NewEnvelopeAsync("Courant", app.AdultA, Visibility.Shared);
        await PatchAsync(app, accounts["Courant A"], new { envelopeId = joint.Value });
        await PatchAsync(app, accounts["Carte A"], new { cardOfAccountId = accounts["Courant A"] });
        return new Setup(app, accounts["Courant A"], accounts["Carte A"], accounts["Livret A"], joint, connection);
    }

    private static async Task PatchAsync(NidApp app, Guid account, object body, PersonId? person = null)
    {
        var response = await app.RawAsync(person ?? app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{account}", body);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> AddAsync(NidApp app, Guid account, string reference, LocalDate? on = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        await store.AddAsync(account, [new NewBankTransaction(reference, on ?? new LocalDate(2026, 10, 5), null, -100m, Currency.Eur, "PHARMACIE DU CENTRE", null, null, "api")], default);
        return (await store.ListAsync([account], null, null, 0, 100, default)).Single(t => t.ProviderRef == reference).Id;
    }

    private static Task<Guid> ReceiptAsync(NidApp app, PersonId actor, Guid line)
        => UploadedAsync(app, actor, Pdf, $"fileName=ticket.pdf&targetKind={Kind}&targetId={line}&role=receipt");

    private static Task<HttpResponseMessage> LinkAsync(NidApp app, PersonId actor, Guid document, Guid line)
        => app.RawAsync(actor, HttpMethod.Post, $"{DocumentApi.Documents}/{document}/links", new { targetKind = Kind, targetId = line, role = "receipt" });

    private static async Task<HttpStatusCode> ReadsAsync(NidApp app, PersonId actor, Guid document) => (await GetAsync(app, actor, document)).StatusCode;

    /// <summary>The documents the operations list shows on the line, as (id, title): none when it shows none, or not the line.</summary>
    private static async Task<(Guid Id, string Title)[]> ListedAsync(NidApp app, PersonId actor, Guid line)
    {
        var item = JsonDocument.Parse(await app.GetRawAsync(actor, $"{BankingApi.Root}/operations")).RootElement.GetProperty("items")
            .EnumerateArray().FirstOrDefault(i => i.GetProperty("id").GetGuid() == line);
        return item.ValueKind == JsonValueKind.Undefined || item.GetProperty("documents").ValueKind == JsonValueKind.Null ? []
            : [.. item.GetProperty("documents").EnumerateArray().Select(d => (d.GetProperty("id").GetGuid(), d.GetProperty("title").GetString()!))];
    }

    /// <summary>The links of the document its reader sees (GET /documents/{id}), and whether « À classer » lists it for them.</summary>
    private static async Task<((string Kind, Guid Id, bool CanWrite)[] Links, bool Inbox)> SeenAsync(NidApp app, PersonId actor, Guid document)
    {
        var links = (await JsonAsync(await GetAsync(app, actor, document))).GetProperty("document").GetProperty("links").EnumerateArray()
            .Select(l => (l.GetProperty("targetKind").GetString()!, l.GetProperty("targetId").GetGuid(), l.GetProperty("canWrite").GetBoolean())).ToArray();
        var inbox = JsonDocument.Parse(await app.GetRawAsync(actor, $"{DocumentApi.Documents}?inbox=true")).RootElement.GetProperty("items")
            .EnumerateArray().Any(i => i.GetProperty("id").GetGuid() == document);
        return (links, inbox);
    }

    private static async Task<Guid?> AnchorAsync(NidApp app, Guid document, Guid line)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IDocumentStore>().LinksOfAsync([document], default))
            .Single(l => l.TargetKind == Kind && l.TargetId == line).AnchorItemId;
    }

    [Fact]
    public async Task A_receipt_on_a_joint_line_is_read_and_opened_by_the_envelope_s_reader_and_listed_on_the_line()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Courant, "a");

        var receipt = await ReceiptAsync(app, app.AdultA, line);

        Assert.Equal(s.Joint.Value, await AnchorAsync(app, receipt, line));
        var detail = await JsonAsync(await GetAsync(app, app.AdultB, receipt));
        var link = Assert.Single(detail.GetProperty("document").GetProperty("links").EnumerateArray());
        Assert.Equal(("receipt", "PHARMACIE DU CENTRE · 05/10/2026 · -100,00 €"), (link.GetProperty("role").GetString(), link.GetProperty("label").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, app.AdultB, receipt, "/content")).StatusCode);
        Assert.Equal([(receipt, "ticket")], await ListedAsync(app, app.AdultB, line));
        Assert.Equal([(receipt, "ticket")], await ListedAsync(app, app.AdultA, line));
        // B reads the envelope but does not write it: no receipt of B's goes on A's line.
        var mine = await UploadedAsync(app, app.AdultB, Pdf, "fileName=b.pdf");
        Assert.Equal(HttpStatusCode.Forbidden, (await LinkAsync(app, app.AdultB, mine, line)).StatusCode);
        // An archived receipt leaves the line's list, as it leaves the documents list.
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{DocumentApi.Documents}/{receipt}/archive")).StatusCode);
        Assert.Empty(await ListedAsync(app, app.AdultA, line));
    }

    [Fact]
    public async Task A_receipt_on_a_line_of_an_account_on_no_envelope_is_read_by_its_uploader_only()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Livret, "a");
        var mine = await UploadedAsync(app, app.AdultB, Pdf, "fileName=b.pdf");
        // B reads nothing of A's own account: the line does not exist for B.
        await AssertRefusedAsync(await LinkAsync(app, app.AdultB, mine, line), HttpStatusCode.NotFound, DocumentsCodes.TargetNotFound);

        var receipt = await ReceiptAsync(app, app.AdultA, line);
        // B's own bank reads the account too: B reads the line, not the receipt A put on it.
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-3", "hash-3", Iban, "Livret A", "SVGS"));

        Assert.Null(await AnchorAsync(app, receipt, line));
        Assert.Equal(HttpStatusCode.NotFound, await ReadsAsync(app, app.AdultB, receipt));
        Assert.Empty(await ListedAsync(app, app.AdultB, line));
        Assert.Equal([(receipt, "ticket")], await ListedAsync(app, app.AdultA, line));
        // Its uploader sees the link outside Opérations too, and the document is filed, not « À classer ».
        var seen = await SeenAsync(app, app.AdultA, receipt);
        Assert.Equal([(Kind, line, true)], seen.Links);
        Assert.False(seen.Inbox);
        // B reads a document through another link of it: that does not show it on this line, whose link B does not read.
        var shared = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=facture.pdf&targetKind=core.item&targetId={s.Joint.Value}&role=invoice");
        Assert.Equal(HttpStatusCode.OK, (await LinkAsync(app, app.AdultA, shared, line)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await ReadsAsync(app, app.AdultB, shared));
        Assert.Empty(await ListedAsync(app, app.AdultB, line));
        // Its uploader takes it off the line again.
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{DocumentApi.Documents}/{receipt}/links/{Kind}/{line}")).StatusCode);
        Assert.Equal([(shared, "facture")], await ListedAsync(app, app.AdultA, line));
    }

    [Fact]
    public async Task Receipts_follow_the_account_and_its_card_to_another_envelope_and_off_it()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Courant, "a");
        var cardLine = await AddAsync(app, s.Carte, "b");
        var receipt = await ReceiptAsync(app, app.AdultA, line);
        var cardReceipt = await ReceiptAsync(app, app.AdultA, cardLine);
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (await ReadsAsync(app, app.AdultB, receipt), await ReadsAsync(app, app.AdultB, cardReceipt)));

        var own = await app.NewEnvelopeAsync("Perso", app.AdultA, Visibility.Private);
        await PatchAsync(app, s.Courant, new { envelopeId = own.Value });

        Assert.Equal((own.Value, own.Value), (await AnchorAsync(app, receipt, line), await AnchorAsync(app, cardReceipt, cardLine)));
        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (await ReadsAsync(app, app.AdultB, receipt), await ReadsAsync(app, app.AdultB, cardReceipt)));
        Assert.Equal(HttpStatusCode.OK, await ReadsAsync(app, app.AdultA, receipt));

        await PatchAsync(app, s.Courant, new { clearEnvelope = true });
        Assert.Equal(((Guid?)null, (Guid?)null), (await AnchorAsync(app, receipt, line), await AnchorAsync(app, cardReceipt, cardLine)));
        // A card taken off its account anchors on nothing of its own either.
        await PatchAsync(app, s.Courant, new { envelopeId = s.Joint.Value });
        Assert.Equal(s.Joint.Value, await AnchorAsync(app, cardReceipt, cardLine));
        await PatchAsync(app, s.Carte, new { clearCard = true });
        Assert.Null(await AnchorAsync(app, cardReceipt, cardLine));
    }

    [Fact]
    public async Task Deleting_the_envelope_leaves_the_receipt_on_the_line_anchored_on_nothing()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Courant, "a");
        var receipt = await ReceiptAsync(app, app.AdultA, line);

        var url = $"/api/v1/investments/envelopes/{s.Joint.Value}";
        var preview = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{url}/deletion")).RootElement;
        // The receipt stays on its line: the delete does not count it among the documents it unlinks.
        Assert.DoesNotContain(preview.GetProperty("removes").EnumerateArray(), r => r.GetProperty("key").GetString() == "documents" && r.GetProperty("count").GetInt32() > 0);
        var version = preview.GetProperty("version").GetUInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{url}?version={version}")).StatusCode);

        Assert.Null(await AnchorAsync(app, receipt, line));
        Assert.Equal(HttpStatusCode.NotFound, await ReadsAsync(app, app.AdultB, receipt));
        Assert.Equal([(receipt, "ticket")], await ListedAsync(app, app.AdultA, line));
        var seen = await SeenAsync(app, app.AdultA, receipt);
        Assert.Equal([(Kind, line, true)], seen.Links);
        Assert.False(seen.Inbox);
    }

    /// <summary>B imports A's statement onto B's envelope, with B's card attached; A's bank connection claims the account (ConnectionUseCases).</summary>
    [Theory]
    [InlineData(Visibility.Private)]
    [InlineData(Visibility.Shared)]
    public async Task A_bank_connection_claiming_an_account_re_anchors_its_receipts_and_its_detached_card_s(Visibility visibility)
    {
        var app = await BankingApi.StartAsync(fixture, overrides: s => s.AddSingleton<IClock>(new FixedClock()));
        await using var _app = app;
        var envelope = await app.NewEnvelopeAsync("Courant de B", app.AdultB, visibility);
        var statement = await app.RawContentAsync(app.AdultB, HttpMethod.Post, $"{BankingApi.Root}/statements", Multipart(Ofx(Iban),
            $"fileName=releve.ofx&confirm=true&envelopeId={envelope.Value}"));
        Assert.True(statement.StatusCode == HttpStatusCode.OK, await statement.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var account = (await JsonAsync(statement)).GetProperty("accountId").GetGuid();
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-c", "hash-c", null, "Carte B", "CARD"));
        var card = (await BankingApi.AccountsAsync(app, app.AdultB)).EnumerateArray().Single(a => a.GetProperty("name").GetString() == "Carte B").GetProperty("id").GetGuid();
        await PatchAsync(app, card, new { cardOfAccountId = account }, app.AdultB);
        var line = await AddAsync(app, account, "a");
        var cardLine = await AddAsync(app, card, "b");
        var receipt = await ReceiptAsync(app, app.AdultB, line);
        var cardReceipt = await ReceiptAsync(app, app.AdultB, cardLine);
        Assert.Equal((envelope.Value, envelope.Value), (await AnchorAsync(app, receipt, line), await AnchorAsync(app, cardReceipt, cardLine)));

        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", Iban, "Courant A"));

        // A private envelope of B's would hide A's own account from A: it goes, and the receipt with it; a shared one stays.
        Assert.Equal(visibility == Visibility.Shared ? envelope.Value : null, await AnchorAsync(app, receipt, line));
        // B's own card does not go with A's account: its line's receipt counts through nothing.
        Assert.Null(await AnchorAsync(app, cardReceipt, cardLine));
    }

    private static byte[] Ofx(string iban) => System.Text.Encoding.UTF8.GetBytes($"""
        <?xml version="1.0" encoding="UTF-8"?>
        <?OFX OFXHEADER="200" VERSION="211" SECURITY="NONE" OLDFILEUID="NONE" NEWFILEUID="NONE"?>
        <OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS>
        <CURDEF>EUR</CURDEF>
        <BANKACCTFROM><ACCTID>{iban}</ACCTID><ACCTTYPE>CHECKING</ACCTTYPE></BANKACCTFROM>
        <BANKTRANLIST><DTSTART>20261012</DTSTART><DTEND>20261012</DTEND><STMTTRN><TRNTYPE>OTHER</TRNTYPE><DTPOSTED>20261012</DTPOSTED><TRNAMT>-10</TRNAMT><FITID>F1</FITID><NAME>BOULANGERIE</NAME></STMTTRN></BANKTRANLIST>
        <LEDGERBAL><BALAMT>100</BALAMT><DTASOF>20261028</DTASOF></LEDGERBAL>
        </STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
        """);

    [Fact]
    public async Task Deleting_the_connection_keeps_the_receipt_on_its_line()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Courant, "a");
        var receipt = await ReceiptAsync(app, app.AdultA, line);

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{BankingApi.Root}/connections/{s.Connection}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, await ReadsAsync(app, app.AdultB, receipt));
        Assert.Equal(s.Joint.Value, await AnchorAsync(app, receipt, line));
    }

    [Fact]
    public async Task A_night_keeps_an_old_line_with_a_receipt()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var kept = await AddAsync(app, s.Courant, "kept", new LocalDate(2025, 1, 1));
        var gone = await AddAsync(app, s.Courant, "gone", new LocalDate(2025, 1, 2));
        await ReceiptAsync(app, app.AdultA, kept);
        await BankingApi.SettingsAsync(app, new { bankRetentionMonths = 12 });

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            Assert.True((await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new SyncAllCommand(), TestContext.Current.CancellationToken)).IsSuccess);
        }

        await using var db = new BankingDbContext(new DbContextOptionsBuilder<BankingDbContext>().Configure(app.ConnectionString).Options);
        var left = await db.Transactions.Select(t => t.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(kept, left);
        Assert.DoesNotContain(gone, left);
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Documents.Application;
using Nid.Documents.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.DocumentApi;

namespace Nid.Documents.IntegrationTests;

/// <summary>Documents tranche 2 design §3: an income phase and a one-off income as link targets, and the links following the rows.</summary>
public class IncomeDocumentTargetTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Sources = "/api/v1/income/sources";
    private const string Phase = "income.phase";
    private const string OneOff = "income.one_off";
    private const string Root = DocumentApi.Documents;

    private static async Task<Guid> CreatedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> PhaseAsync(NidApp app, PersonId owner, ItemId source, string label, string startsOn, string? note)
        => await CreatedAsync(await app.RawAsync(owner, HttpMethod.Post, $"{Sources}/{source.Value}/phases", new
        {
            label, startsOn, netAmount = new { amount = "2000", currency = "EUR" }, every = new { count = 1, unit = "month" }, note,
        }));

    private static object OneOffBody(string occurredOn) => new { occurredOn, amount = new { amount = "500", currency = "EUR" }, note = "Prime" };

    private static async Task<Guid> OneOffAsync(NidApp app, PersonId owner, ItemId source, string occurredOn)
        => await CreatedAsync(await app.RawAsync(owner, HttpMethod.Post, $"{Sources}/{source.Value}/one-offs", OneOffBody(occurredOn)));

    private static Task<Guid> LinkedAsync(NidApp app, PersonId actor, string kind, Guid target, string role = "payslip")
        => UploadedAsync(app, actor, Pdf, $"fileName=a.pdf&targetKind={kind}&targetId={target}&role={role}");

    private static Task<HttpResponseMessage> LinkAsync(NidApp app, PersonId actor, Guid document, string kind, Guid target)
        => app.RawAsync(actor, HttpMethod.Post, $"{Root}/{document}/links", new { targetKind = kind, targetId = target, role = "payslip" });

    private static async Task<(string Kind, Guid Id, string Label)[]> LinksAsync(NidApp app, PersonId actor, Guid document)
    {
        var response = await GetAsync(app, actor, document);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("document").GetProperty("links").EnumerateArray()
            .Select(l => (l.GetProperty("targetKind").GetString()!, l.GetProperty("targetId").GetGuid(), l.GetProperty("label").GetString()!))
            .ToArray();
    }

    private static async Task<Guid[]> ListedAsync(NidApp app, PersonId actor, string query)
    {
        var response = await app.RawAsync(actor, HttpMethod.Get, $"{Root}?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();
    }

    private static async Task<IReadOnlyList<LinkRecord>> StoredLinksAsync(NidApp app, Guid document)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDocumentStore>().LinksOfAsync([document], default);
    }

    [Fact]
    public async Task A_shared_sources_phase_opens_the_document_to_B_under_its_note_or_its_source_and_start()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await app.NewIncomeSourceAsync("Salaire A", app.AdultA);
        var noted = await PhaseAsync(app, app.AdultA, source, "Salaire", "2026-09-01", "Bulletin 09/2026");
        var bare = await PhaseAsync(app, app.AdultA, source, "Prime", "2025-01-01", null);

        var payslip = await LinkedAsync(app, app.AdultA, Phase, noted);
        var other = await LinkedAsync(app, app.AdultA, Phase, bare);

        Assert.Equal([(Phase, noted, "Bulletin 09/2026")], await LinksAsync(app, app.AdultB, payslip));
        Assert.Equal([(Phase, bare, "Salaire A · 01/01/2025")], await LinksAsync(app, app.AdultB, other));
        Assert.Equal(source.Value, Assert.Single(await StoredLinksAsync(app, payslip)).AnchorItemId);
    }

    [Fact]
    public async Task A_private_sources_phase_keeps_the_document_from_B_and_lists_it_for_A()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await app.NewIncomeSourceAsync("Salaire A", app.AdultA, visibility: "private");
        var phase = await PhaseAsync(app, app.AdultA, source, "Salaire", "2025-01-01", null);

        var id = await LinkedAsync(app, app.AdultA, Phase, phase);

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(app, app.AdultB, id)).StatusCode);
        // A row's screen lists its readable documents whatever the scope: the default one, household, keeps a private row's too.
        Assert.Equal([id], await ListedAsync(app, app.AdultA, $"targetKind={Phase}&targetId={phase}"));
        Assert.Empty(await ListedAsync(app, app.AdultB, $"targetKind={Phase}&targetId={phase}"));
    }

    [Fact]
    public async Task B_who_reads_the_shared_source_cannot_link_to_its_phase()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await app.NewIncomeSourceAsync("Salaire A", app.AdultA);
        var phase = await PhaseAsync(app, app.AdultA, source, "Salaire", "2025-01-01", null);
        var mine = await UploadedAsync(app, app.AdultB, Pdf);

        Assert.Equal(HttpStatusCode.Forbidden, (await LinkAsync(app, app.AdultB, mine, Phase, phase)).StatusCode);
        Assert.Empty(await StoredLinksAsync(app, mine));
    }

    [Fact]
    public async Task A_one_off_is_labelled_by_its_source_its_date_and_its_kind()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await app.NewIncomeSourceAsync("Salaire A", app.AdultA);
        var flow = await OneOffAsync(app, app.AdultA, source, "2025-06-10");

        var id = await LinkedAsync(app, app.AdultA, OneOff, flow);

        Assert.Equal([(OneOff, flow, "Salaire A · 10/06/2025 · revenu exceptionnel")], await LinksAsync(app, app.AdultB, id));
    }

    [Fact]
    public async Task A_flow_that_is_not_an_income_one_off_is_not_a_target()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var contribution = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{envelope.Value}/flows",
            new { occurredOn = "2025-01-10", kind = "contribution", amount = new { amount = "90.00", currency = "EUR" } }));
        var envelopeIncome = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{envelope.Value}/flows",
            new { occurredOn = "2025-02-10", kind = "income", category = "one_off", amount = new { amount = "40.00", currency = "EUR" } }));
        var source = await app.NewIncomeSourceAsync("Salaire A", app.AdultA);
        Guid uncategorised;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
            uncategorised = (await scope.ServiceProvider.GetRequiredService<IFlows>().RecordAsync(
                new NewFlow(source, new LocalDate(2025, 6, 20), FlowKind.Income, Money.Create(70m, Currency.Eur).Ok(), null, null, "autre"),
                default)).Ok().Id;
        }
        var id = await UploadedAsync(app, app.AdultA, Pdf);

        await AssertRefusedAsync(await LinkAsync(app, app.AdultA, id, OneOff, contribution), HttpStatusCode.NotFound, DocumentsCodes.TargetNotFound);
        await AssertRefusedAsync(await LinkAsync(app, app.AdultA, id, OneOff, envelopeIncome), HttpStatusCode.NotFound, DocumentsCodes.TargetNotFound);
        await AssertRefusedAsync(await LinkAsync(app, app.AdultA, id, OneOff, uncategorised), HttpStatusCode.NotFound, DocumentsCodes.TargetNotFound);
        Assert.Empty(await StoredLinksAsync(app, id));
    }

    [Fact]
    public async Task Deleting_a_phase_or_a_one_off_unlinks_its_documents_and_keeps_them()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await app.NewIncomeSourceAsync("Salaire A", app.AdultA);
        var phase = await PhaseAsync(app, app.AdultA, source, "Salaire", "2025-01-01", null);
        var flow = await OneOffAsync(app, app.AdultA, source, "2025-06-10");
        var payslip = await LinkedAsync(app, app.AdultA, Phase, phase);
        var bonus = await LinkedAsync(app, app.AdultA, OneOff, flow, "statement");

        // A stale version fails the deletion: the link stays with the phase.
        Assert.Equal(HttpStatusCode.Conflict, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Sources}/{source.Value}/phases/{phase}?version=999")).StatusCode);
        Assert.Single(await StoredLinksAsync(app, payslip));

        var version = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{source.Value}"))).GetProperty("phases")
            .EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == phase).GetProperty("version").GetUInt32();
        Assert.Equal(HttpStatusCode.NoContent,
            (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Sources}/{source.Value}/phases/{phase}?version={version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Sources}/{source.Value}/one-offs/{flow}")).StatusCode);

        foreach (var id in new[] { payslip, bonus })
        {
            Assert.Empty(await LinksAsync(app, app.AdultA, id));
            Assert.Empty(await StoredLinksAsync(app, id));
        }
    }

    [Fact]
    public async Task Correcting_a_one_off_moves_its_links_to_the_new_flow()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await app.NewIncomeSourceAsync("Salaire A", app.AdultA);
        var flow = await OneOffAsync(app, app.AdultA, source, "2025-06-10");
        var id = await LinkedAsync(app, app.AdultA, OneOff, flow);

        var corrected = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Sources}/{source.Value}/one-offs/{flow}", OneOffBody("2025-06-12"));
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        var next = (await JsonAsync(corrected)).GetProperty("id").GetGuid();

        Assert.NotEqual(flow, next);
        Assert.Equal([(OneOff, next, "Salaire A · 12/06/2025 · revenu exceptionnel")], await LinksAsync(app, app.AdultA, id));
        Assert.Equal(source.Value, Assert.Single(await StoredLinksAsync(app, id)).AnchorItemId);
    }

    [Fact]
    public async Task A_document_linked_only_to_Bs_private_phase_or_one_off_never_shows_to_A()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await app.NewIncomeSourceAsync("CoffreSecretB", app.AdultB, visibility: "private");
        var phase = await PhaseAsync(app, app.AdultB, source, "Salaire", "2025-01-01", "NoteSecreteB");
        var flow = await OneOffAsync(app, app.AdultB, source, "2025-06-10");
        var onPhase = await LinkedAsync(app, app.AdultB, Phase, phase);
        var onOneOff = await LinkedAsync(app, app.AdultB, OneOff, flow);
        Assert.Equal([(Phase, phase, "NoteSecreteB")], await LinksAsync(app, app.AdultB, onPhase));
        Assert.Equal([(OneOff, flow, "CoffreSecretB · 10/06/2025 · revenu exceptionnel")], await LinksAsync(app, app.AdultB, onOneOff));

        foreach (var (id, kind, target) in new[] { (onPhase, Phase, phase), (onOneOff, OneOff, flow) })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(app, app.AdultA, id)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(app, app.AdultA, id, "/content")).StatusCode);
            foreach (var url in new[]
                     {
                         $"{Root}?scope=household", $"{Root}?scope=me", $"{Root}?inbox=true", $"{Root}?scope=household&archived=true",
                         $"{Root}/targets", $"{Root}?targetKind={kind}&targetId={target}", $"{Root}?scope=me&targetKind={kind}&targetId={target}",
                     })
            {
                var raw = await app.GetRawAsync(app.AdultA, url);
                Assert.DoesNotContain(id.ToString(), raw);
                Assert.DoesNotContain(target.ToString(), raw);
                Assert.DoesNotContain("CoffreSecretB", raw);
                Assert.DoesNotContain("NoteSecreteB", raw);
            }
            Assert.Empty(await ListedAsync(app, app.AdultA, $"targetKind={kind}&targetId={target}"));

            // A's own document cannot be linked to it either: the target answers as a missing one, not a read-only one.
            var mine = await UploadedAsync(app, app.AdultA, Pdf);
            await AssertRefusedAsync(await LinkAsync(app, app.AdultA, mine, kind, target), HttpStatusCode.NotFound, DocumentsCodes.TargetNotFound);
            Assert.Empty(await StoredLinksAsync(app, mine));
        }
    }

    [Fact]
    public async Task Anchor_lists_documents_linked_to_the_source_its_phases_and_its_one_offs()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await app.NewIncomeSourceAsync("Salaire A", app.AdultA);
        var other = await app.NewIncomeSourceAsync("Salaire B", app.AdultA);
        var phase = await PhaseAsync(app, app.AdultA, source, "Salaire", "2025-01-01", null);
        var flow = await OneOffAsync(app, app.AdultA, source, "2025-06-10");
        var onPhase = await LinkedAsync(app, app.AdultA, Phase, phase);
        var onOneOff = await LinkedAsync(app, app.AdultA, OneOff, flow);
        var onItem = await LinkedAsync(app, app.AdultA, "core.item", source.Value);
        await LinkedAsync(app, app.AdultA, "core.item", other.Value);

        var listed = await ListedAsync(app, app.AdultA, $"anchor={source.Value}");

        Assert.Equal(new[] { onPhase, onOneOff, onItem }.Order(), listed.Order());
        Assert.Equal(Phase, (await LinksAsync(app, app.AdultA, onPhase)).Single().Kind);
    }

    [Fact]
    public async Task Each_link_carries_its_anchor_so_an_item_tab_keeps_its_own()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await app.NewIncomeSourceAsync("Salaire A", app.AdultA);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var phase = await PhaseAsync(app, app.AdultA, source, "Salaire", "2025-01-01", null);
        var document = await LinkedAsync(app, app.AdultA, "core.item", envelope.Value);
        Assert.True((await LinkAsync(app, app.AdultA, document, Phase, phase)).IsSuccessStatusCode);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Root}?anchor={envelope.Value}");
        var links = (await JsonAsync(response)).GetProperty("items").EnumerateArray().Single().GetProperty("links").EnumerateArray()
            .Select(l => (l.GetProperty("targetKind").GetString()!, l.GetProperty("anchorItemId").GetGuid())).ToArray();

        Assert.Equal(new[] { ("core.item", envelope.Value), (Phase, source.Value) }.Order(), links.Order());
    }

    [Fact]
    public async Task Anchor_on_a_private_source_is_empty_for_the_other_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await app.NewIncomeSourceAsync("Salaire A", app.AdultA, visibility: "private");
        var phase = await PhaseAsync(app, app.AdultA, source, "Salaire", "2025-01-01", null);
        var payslip = await LinkedAsync(app, app.AdultA, Phase, phase);

        Assert.Empty(await ListedAsync(app, app.AdultB, $"anchor={source.Value}"));
        Assert.Equal([payslip], await ListedAsync(app, app.AdultA, $"anchor={source.Value}"));
    }

    [Fact]
    public async Task Anchor_combined_with_inbox_or_target_is_a_bad_request()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await app.NewIncomeSourceAsync("Salaire A", app.AdultA);

        foreach (var extra in new[] { "inbox=true", $"targetKind=core.item&targetId={source.Value}" })
            Assert.Equal(HttpStatusCode.BadRequest,
                (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Root}?anchor={source.Value}&{extra}")).StatusCode);
    }

    [Fact]
    public async Task Anchor_skips_archived_documents()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await app.NewIncomeSourceAsync("Salaire A", app.AdultA);
        var id = await LinkedAsync(app, app.AdultA, "core.item", source.Value);
        Assert.Equal([id], await ListedAsync(app, app.AdultA, $"anchor={source.Value}"));

        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Root}/{id}/archive");

        Assert.Empty(await ListedAsync(app, app.AdultA, $"anchor={source.Value}"));
    }
}

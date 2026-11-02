using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Documents.Application;
using Nid.Documents.Contracts;
using Nid.Investments.Application;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Application;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Host.IntegrationTests.LeakBench;

/// <summary>
/// The dataset of access-tests.md §2: adults A and B, dependent D, and one item of every ownership and
/// visibility shape. Labels are distinctive so a leak can be caught by searching the serialised response.
/// </summary>
public sealed class MixedDataset
{
    public const string PrivateLabelA = "ZZPRIVATEALPHA";
    public const string PrivateLabelB = "ZZPRIVATEBETA";

    /// <summary>The reference income of B's solo 2018 notice: distinctive, so the textual scan finds it in a read of A.</summary>
    public const string NoticeFigureB = "987654.32";

    /// <summary>
    /// One row of the table in access-tests.md §2, transcribed. <see cref="Readers"/> and
    /// <see cref="Writers"/> are the columns of that table — the specification — and are NOT computed by
    /// calling <c>AccessRules</c>, so the bench compares the running system against the document rather
    /// than against itself. <c>AccessRulesPropertyTests</c> checks the rules themselves.
    /// </summary>
    private sealed record Row(
        string Name, string Label, Visibility Visibility, string[] Owners, string[] Readers, string[] Writers, ShareLevel Level = ShareLevel.View);

    private static readonly Row[] Rows =
    [
        new("S-A", "shared-a", Visibility.Shared, ["A"], ["A", "B"], ["A"]),
        new("S-B", "shared-b", Visibility.Shared, ["B"], ["A", "B"], ["B"]),
        new("J-AB", "joint-ab", Visibility.Shared, ["A", "B"], ["A", "B"], ["A", "B"]),
        new("P-A", PrivateLabelA, Visibility.Private, ["A"], ["A"], ["A"]),
        new("P-B", PrivateLabelB, Visibility.Private, ["B"], ["B"], ["B"]),
        new("D", "dependent-owned", Visibility.Shared, ["D"], ["A", "B"], ["A", "B"]),
        new("M-AD", "mixed-ad", Visibility.Shared, ["A", "D"], ["A", "B"], ["A", "B"]),
        new("E-A", "edit-a", Visibility.Shared, ["A"], ["A", "B"], ["A", "B"], ShareLevel.Edit),
    ];

    private readonly Dictionary<ItemId, string> names;

    private MixedDataset(
        NidApp app, IReadOnlyDictionary<string, ItemId> items, IReadOnlyDictionary<string, ItemId> envelopes,
        IReadOnlyDictionary<string, ItemId> incomeSources, IReadOnlyDictionary<string, ItemId> loans,
        IReadOnlyDictionary<string, ItemId> properties, IReadOnlyDictionary<string, ItemId> scenarios,
        IReadOnlyDictionary<string, ItemId> projectionScenarios, IReadOnlyDictionary<string, ItemId> goals,
        IReadOnlyDictionary<string, IReadOnlyList<Guid>> proposals, IReadOnlyDictionary<ItemId, Guid> documents, IReadOnlyDictionary<ItemId, Guid> unlinkDocuments)
    {
        (App, Items, Envelopes, IncomeSources, Loans, Properties, Scenarios, ProjectionScenarios, Goals, Proposals, Documents, UnlinkDocuments) =
            (app, items, envelopes, incomeSources, loans, properties, scenarios, projectionScenarios, goals, proposals, documents, unlinkDocuments);
        names = items.ToDictionary(kv => kv.Value, kv => kv.Key);
        foreach (var (name, envelope) in envelopes)
            names[envelope] = name + " (envelope)";
        foreach (var (name, source) in incomeSources)
            names[source] = name + " (income)";
        foreach (var (name, loan) in loans)
            names[loan] = name + " (loan)";
        foreach (var (name, property) in properties)
            names[property] = name + " (property)";
        foreach (var (name, scenario) in scenarios)
            names[scenario] = name + " (scenario)";
        foreach (var (name, projection) in projectionScenarios)
            names[projection] = name + " (projection)";
        foreach (var (name, goal) in goals)
            names[goal] = name + " (goal)";
    }

    public NidApp App { get; }

    /// <summary>Keyed by the names of access-tests.md §2: S-A, S-B, J-AB, P-A, P-B, D, M-AD.</summary>
    public IReadOnlyDictionary<string, ItemId> Items { get; }

    /// <summary>
    /// The envelope twin of each row (2B1a): same §2 name, label, owners and visibility, so the table expects of
    /// it exactly what it expects of the row's subscription. The Investments requests run against these.
    /// </summary>
    public IReadOnlyDictionary<string, ItemId> Envelopes { get; }

    /// <summary>
    /// The income twin of each row (2C1): same §2 name, label, owners and visibility, one phase. The Income requests run
    /// against these.
    /// </summary>
    public IReadOnlyDictionary<string, ItemId> IncomeSources { get; }

    /// <summary>The loan twin of each row (3A2): same §2 name, label, owners and visibility. The loan requests run against these.</summary>
    public IReadOnlyDictionary<string, ItemId> Loans { get; }

    /// <summary>The property twin of each row (3C1): same §2 name, label, owners and visibility. The property requests run against these.</summary>
    public IReadOnlyDictionary<string, ItemId> Properties { get; }

    /// <summary>The scenario twin of each row (property purchase spec §5): same §2 name, label, owners and visibility. The scenario requests run against these.</summary>
    public IReadOnlyDictionary<string, ItemId> Scenarios { get; }

    /// <summary>
    /// The projection scenario twin of each row (net worth projection spec §6): same §2 name, label, owners and visibility, no event. The
    /// projection scenario requests run against these.
    /// </summary>
    public IReadOnlyDictionary<string, ItemId> ProjectionScenarios { get; }

    /// <summary>
    /// The savings goal twin of each row: same §2 name, label, owners and visibility, drawing half of the row's envelope twin; S-A and S-B also
    /// draw on their owner's private envelope twin. The goal requests run against these.
    /// </summary>
    public IReadOnlyDictionary<string, ItemId> Goals { get; }

    /// <summary>
    /// 4D1 D9: one pending proposal of each kind (valuation, statement, payslip, invoice, amortisation table) on each private row's twins, keyed
    /// "P-A" and "P-B", their texts carrying the row's label: the textual scan catches one shown to the other adult.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Guid>> Proposals { get; }

    /// <summary>
    /// The document of each row, keyed by the row's subscription: uploaded by its first adult owner (<see cref="UploaderOf"/>), its title
    /// and file name carrying the row's label, linked (<c>core.item</c>, <c>statement</c>) to that subscription. P-A's and P-B's are each
    /// adult's private document. S-A's and S-B's are also linked to their uploader's private subscription (spec §7): the other adult reads
    /// them through the shared link, and the scans find the private link's label if it is ever shown. The Documents requests run against these.
    /// </summary>
    public IReadOnlyDictionary<ItemId, Guid> Documents { get; }

    /// <summary>
    /// The phase of P-A's and P-B's income twins, keyed by row. Each owner's document is linked (<c>income.phase</c>, <c>payslip</c>)
    /// to that phase alone, and its title carries the private label, so the scans catch it if it is ever shown to the other adult.
    /// </summary>
    public IReadOnlyDictionary<string, Guid> PrivatePhases { get; private init; } = new Dictionary<string, Guid>();

    /// <summary>Tax assessments spec §6: B's solo 2018 notice and its source PDF, B's private document (linked to B alone).</summary>
    public (Guid Notice, Guid Document) NoticeB { get; private init; }

    /// <summary>A's line on the joint account, linked to A's private subscription twin: B reads the line, never its link.</summary>
    public Guid JointLine { get; private init; }

    /// <summary>As <see cref="PrivatePhases"/> for each private source's one-off income, by the source's row name.</summary>
    public IReadOnlyDictionary<string, Guid> PrivateOneOffs { get; private init; } = new Dictionary<string, Guid>();

    /// <summary>A second document of each row, as <see cref="Documents"/> but linked to its row only, for the unlink entry alone to remove.</summary>
    public IReadOnlyDictionary<ItemId, Guid> UnlinkDocuments { get; }

    public ItemId this[string name] => Items[name];

    /// <summary>The sale tax estimate's items: each row's property twin under its §2 name, and its envelope twin (« S-A (envelope) »…).</summary>
    private IReadOnlyDictionary<string, ItemId> SaleItems
        => Properties.Concat(Envelopes.Select(kv => KeyValuePair.Create(kv.Key + " (envelope)", kv.Value))).ToDictionary();

    /// <summary>
    /// The rows a request is run against: the envelope twins for a request of <c>Nid.Investments.Application</c>
    /// (a subscription is not an envelope, so it would only ever be refused on its own merits), the income twins for
    /// one of <c>Nid.Income.Application</c>, the goal twins for one of <c>Nid.Goals.Application</c>, the property and envelope
    /// twins for the sale tax estimate, the subscriptions for every other one.
    /// </summary>
    public IReadOnlyDictionary<string, ItemId> ItemsFor(Type requestType)
        => requestType == typeof(Nid.Core.Application.Tax.GetSaleTaxQuery) ? SaleItems
            : requestType == typeof(ListBankTransactionsQuery) ? Envelopes
            : requestType.Assembly == typeof(Nid.Investments.Application.ListEnvelopesQuery).Assembly ? Envelopes
            : requestType.Assembly == typeof(Nid.Income.Application.ListIncomeSourcesQuery).Assembly ? IncomeSources
            : requestType.Assembly == typeof(Nid.Goals.Application.CreateGoalCommand).Assembly ? Goals
            : requestType.Assembly == typeof(Nid.RealEstate.Application.ListLoansQuery).Assembly
                // Every property request carries « Propert » in its name (ListPropertiesQuery, RecordPropertyValuationCommand…).
                ? (requestType.Name.Contains("Propert", StringComparison.Ordinal) ? Properties : Loans)
            : requestType.Namespace == typeof(Nid.Core.Application.Scenarios.ListScenariosQuery).Namespace ? Scenarios
            : requestType.Namespace == typeof(Nid.Core.Application.Projection.GetProjectionScenarioQuery).Namespace ? ProjectionScenarios
            : Items;

    public static async Task<MixedDataset> CreateAsync(PostgresFixture fixture)
    {
        // Banking on (a key for the host, an application id below), so the bench's banking requests run rather than meet banking.disabled.
        var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string> { ["EnableBanking:PrivateKey"] = "test-key" });

        var items = new Dictionary<string, ItemId>();
        var envelopes = new Dictionary<string, ItemId>();
        var incomeSources = new Dictionary<string, ItemId>();
        var loans = new Dictionary<string, ItemId>();
        var properties = new Dictionary<string, ItemId>();
        var scenarios = new Dictionary<string, ItemId>();
        var projectionScenarios = new Dictionary<string, ItemId>();
        var goals = new Dictionary<string, ItemId>();
        var documents = new Dictionary<ItemId, Guid>();
        var unlinkDocuments = new Dictionary<ItemId, Guid>();
        foreach (var row in Rows)
        {
            var owners = row.Owners
                .Select(o => (Person: PersonOf(app, o), ShareBp: 10_000 / row.Owners.Length))
                .ToArray();
            items[row.Name] = await app.NewOwnedSubscriptionAsync(row.Label, row.Visibility, owners);
            envelopes[row.Name] = await app.NewEnvelopeAsync(row.Label, row.Visibility, owners);
            // Its one phase carries the label in its note, so the textual scan catches a phase read by the wrong adult.
            incomeSources[row.Name] = await app.NewIncomeSourceAsync(row.Label, row.Visibility, owners);
            // Named after the label, so the textual scan catches a loan read by the wrong adult.
            loans[row.Name] = await app.NewLoanAsync(row.Label, row.Visibility, owners);
            properties[row.Name] = await app.NewPropertyAsync(row.Label, row.Visibility, owners);
            goals[row.Name] = await app.NewGoalAsync(row.Label, owners.Select(o => o.Person).FirstOrDefault(p => p == app.AdultA || p == app.AdultB, app.AdultA),
                row.Visibility, [(envelopes[row.Name], 5000)], owners);

            var creator = owners.Select(o => o.Person).FirstOrDefault(p => p == app.AdultA || p == app.AdultB, app.AdultA);
            var (scenario, scenarioRefused) = await app.InvokeAsync(creator, new Nid.Core.Application.Scenarios.CreateScenarioCommand(
                row.Label, row.Visibility, owners.Select(o => new OwnerShare(o.Person, o.ShareBp)).ToList(), VisibilityScope.Me,
                RequestCatalogue.BenchScenario([])));
            scenarios[row.Name] = (ItemId?)scenario ?? throw new InvalidOperationException($"Seeding {row.Name}'s scenario failed: {scenarioRefused!.Code}");
            var (projection, projectionRefused) = await app.InvokeAsync(creator, new Nid.Core.Application.Projection.CreateProjectionScenarioCommand(
                row.Label, row.Visibility, owners.Select(o => new OwnerShare(o.Person, o.ShareBp)).ToList(), VisibilityScope.Me,
                RequestCatalogue.BenchProjection()));
            projectionScenarios[row.Name] = (ItemId?)projection
                ?? throw new InvalidOperationException($"Seeding {row.Name}'s projection scenario failed: {projectionRefused!.Code}");

            // #294: the row's level on every twin, as its owner's « Partage » dialog would set it.
            if (row.Level == ShareLevel.Edit)
                await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
                {
                    Guid[] twins = [items[row.Name].Value, envelopes[row.Name].Value, incomeSources[row.Name].Value, loans[row.Name].Value,
                        properties[row.Name].Value, goals[row.Name].Value, scenarios[row.Name].Value, projectionScenarios[row.Name].Value];
                    await core.Items.Where(i => twins.Contains(i.Id)).ExecuteUpdateAsync(s => s.SetProperty(i => i.ShareLevel, "edit"));
                }

            // One value and one flow per twin, through the real requests: without facts the valuations and flows
            // export pages are empty for everyone and the output guard would check nothing on them. The label in
            // the notes lets the textual scan catch a fact exported to the wrong reader.
            var writer = owners.Select(o => o.Person).FirstOrDefault(p => p == app.AdultA || p == app.AdultB, app.AdultA);
            var (_, valueRefused) = await app.InvokeAsync(writer, new RecordValuationCommand(
                envelopes[row.Name], new LocalDate(2025, 3, 31), Money.Create(1000m, Currency.Eur).Ok(), row.Label));
            var (_, flowRefused) = await app.InvokeAsync(writer, new RecordFlowCommand(
                envelopes[row.Name], new FlowDraft(new LocalDate(2025, 3, 1), "contribution", Money.Create(900m, Currency.Eur).Ok(), null, null, row.Label)));
            if (valueRefused is not null || flowRefused is not null)
                throw new InvalidOperationException($"Seeding {row.Name}'s facts failed: {(valueRefused ?? flowRefused)!.Code}");

            // A private twin's holding with an ISIN and a breakdown, so the response scan of the breakdown requests sees a real one.
            if (row.Visibility == Visibility.Private)
            {
                var (holding, holdingRefused) = await app.InvokeAsync(writer, new AddHoldingCommand(
                    envelopes[row.Name], new HoldingDraft(row.Label, row.Name == "P-A" ? "FR0000120271" : "FR0000131104", "fund", null, null, null, null, null, null, null, null)));
                var (_, breakdownRefused) = holdingRefused is null
                    ? await app.InvokeAsync(writer, new RecordHoldingBreakdownCommand(envelopes[row.Name], (Guid)holding!, new LocalDate(2025, 3, 31),
                        [new BreakdownPart("france", 100m)], [new BreakdownPart("financials", 100m)]))
                    : (null, holdingRefused);
                if (breakdownRefused is not null)
                    throw new InvalidOperationException($"Seeding {row.Name}'s breakdown failed: {breakdownRefused.Code}");
            }

            // The property twin's estimate and flow too, the label in their notes: a property read returning another item's facts is caught.
            var (_, estimateRefused) = await app.InvokeAsync(writer, new Nid.RealEstate.Application.RecordPropertyValuationCommand(
                properties[row.Name], new LocalDate(2025, 3, 31), 1000m, row.Label));
            var (_, expenseRefused) = await app.InvokeAsync(writer, new Nid.RealEstate.Application.RecordPropertyFlowCommand(
                properties[row.Name], new Nid.RealEstate.Application.PropertyFlowDraft(new LocalDate(2025, 3, 1), "expense", 900m, "other", row.Label)));
            if (estimateRefused is not null || expenseRefused is not null)
                throw new InvalidOperationException($"Seeding {row.Name}'s property facts failed: {(estimateRefused ?? expenseRefused)!.Code}");

            // And its rental (3C2): a planned cost noted with the label and a tenancy labelled with it, after the twin's purchase (2020-01-15).
            // 3E1: the tax falls due today (2025-10-15) and the tenancy ends on 2025-11-30, so both reach « À venir » and its scan.
            var (_, costRefused) = await app.InvokeAsync(writer, new Nid.RealEstate.Application.AddPropertyRecurringCostCommand(
                properties[row.Name], new Nid.RealEstate.Application.RecurringCostDraft(
                    "property_tax", 800m, 1, DurationUnit.Year, new LocalDate(2020, 10, 15), null, row.Label)));
            var (_, tenancyRefused) = await app.InvokeAsync(writer, new Nid.RealEstate.Application.AddPropertyTenancyCommand(
                properties[row.Name], new Nid.RealEstate.Contracts.Tenancy(row.Label, new LocalDate(2020, 2, 1), new LocalDate(2025, 11, 30), 500m, 0m, null, null)));
            if (costRefused is not null || tenancyRefused is not null)
                throw new InvalidOperationException($"Seeding {row.Name}'s rental failed: {(costRefused ?? tenancyRefused)!.Code}");

            foreach (var seeded in new[] { documents, unlinkDocuments })
            {
                var (document, documentRefused) = await app.InvokeAsync(writer, new UploadDocumentCommand(DocumentApi.Pdf,
                    $"{row.Label}.pdf", row.Label, null, null, new DocumentTarget(TargetKinds.CoreItem, items[row.Name].Value), DocumentRoles.Statement));
                seeded[items[row.Name]] = (document as UploadedDocument)?.Id ?? throw new InvalidOperationException($"Seeding {row.Name}'s document failed: {documentRefused!.Code}");
            }
        }

        foreach (var (shared, hidden, owner) in new[] { ("S-A", "P-A", app.AdultA), ("S-B", "P-B", app.AdultB) })
        {
            var (_, refused) = await app.InvokeAsync(owner, new LinkDocumentCommand(
                documents[items[shared]], new DocumentTarget(TargetKinds.CoreItem, items[hidden].Value), DocumentRoles.Statement));
            if (refused is not null)
                throw new InvalidOperationException($"Linking {shared}'s document to {hidden} failed: {refused.Code}");
        }

        // #108: each adult's shared envelope and property twins cite that adult's private document on every value and flow, as an approved
        // proposal would: the other adult reads the facts, never the document, so its id is in no read of theirs (ForbiddenDocument).
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            foreach (var (shared, hidden) in new[] { ("S-A", "P-A"), ("S-B", "P-B") })
            {
                var twins = new[] { envelopes[shared].Value, properties[shared].Value };
                var document = documents[items[hidden]];
                await core.Valuations.Where(v => twins.Contains(v.ItemId)).ExecuteUpdateAsync(s => s.SetProperty(v => v.DocumentId, document));
                await core.Flows.Where(f => twins.Contains(f.ItemId)).ExecuteUpdateAsync(s => s.SetProperty(f => f.DocumentId, document));
            }

        var privatePhases = new Dictionary<string, Guid>();
        var privateOneOffs = new Dictionary<string, Guid>();
        foreach (var (hidden, owner) in new[] { ("P-A", app.AdultA), ("P-B", app.AdultB) })
        {
            var detail = JsonDocument.Parse(await app.GetRawAsync(owner, $"/api/v1/income/sources/{incomeSources[hidden].Value}")).RootElement;
            var phase = detail.GetProperty("phases")[0].GetProperty("id").GetGuid();
            var label = Rows.Single(r => r.Name == hidden).Label;
            var (_, refused) = await app.InvokeAsync(owner, new UploadDocumentCommand(DocumentApi.Pdf,
                $"{label}-payslip.pdf", label, null, null, new DocumentTarget("income.phase", phase), DocumentRoles.Payslip));
            if (refused is not null)
                throw new InvalidOperationException($"Seeding {hidden}'s phase document failed: {refused.Code}");
            privatePhases[hidden] = phase;

            var created = await app.RawAsync(owner, HttpMethod.Post, $"/api/v1/income/sources/{incomeSources[hidden].Value}/one-offs",
                new { occurredOn = "2025-06-10", amount = new { amount = "500", currency = "EUR" }, note = "Prime" });
            if (created.StatusCode != System.Net.HttpStatusCode.Created)
                throw new InvalidOperationException($"Seeding {hidden}'s one-off failed: {created.StatusCode}");
            var oneOff = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
            var (_, oneOffRefused) = await app.InvokeAsync(owner, new UploadDocumentCommand(DocumentApi.Pdf,
                $"{label}-bonus.pdf", label, null, null, new DocumentTarget("income.one_off", oneOff), DocumentRoles.Statement));
            if (oneOffRefused is not null)
                throw new InvalidOperationException($"Seeding {hidden}'s one-off document failed: {oneOffRefused.Code}");
            privateOneOffs[hidden] = oneOff;
        }

        // Each private item gets a custom provider named after its label, through the real requests: a provider list
        // that carried it to the other adult would put the forbidden label in the textual leak scan.
        foreach (var (name, owner, label) in new[] { ("P-A", app.AdultA, PrivateLabelA), ("P-B", app.AdultB, PrivateLabelB) })
        {
            var (created, refused) = await app.InvokeAsync(owner, new CreateProviderCommand(label + " provider", null, null, [CatalogLimits.OtherLabel]));
            if (refused is not null)
                throw new InvalidOperationException($"Seeding {name}'s provider failed: {refused.Code}");
            var (_, notLinked) = await app.InvokeAsync(owner, new EditSubscriptionCommand(items[name], null, null, false, null, ((ProviderView)created!).Key));
            if (notLinked is not null)
                throw new InvalidOperationException($"Linking {name}'s provider failed: {notLinked.Code}");
        }

        // Each loan twin is linked to its row's property twin, except the private ones, linked to their owner's shared property:
        // the other adult reads that property, and the textual scan finds the private loan's label in it if it is ever shown.
        foreach (var row in Rows)
        {
            var property = properties[row.Name switch { "P-A" => "S-A", "P-B" => "S-B", var same => same }];
            var writer = row.Owners.Select(o => PersonOf(app, o)).FirstOrDefault(p => p == app.AdultA || p == app.AdultB, app.AdultA);
            var (_, refused) = await app.InvokeAsync(writer, new Nid.RealEstate.Application.EditLoanCommand(
                loans[row.Name], null, new Nid.RealEstate.Contracts.LoanDetailsChange(PropertyId: property)));
            if (refused is not null)
                throw new InvalidOperationException($"Linking {row.Name}'s loan failed: {refused.Code}");
        }

        // Each shared goal twin of one adult also draws on that adult's private envelope twin: the other adult reads the goal, and the
        // textual scan finds the private label in it if the envelope is ever shown (D1). 4000 bp leave room for the bench's 1 bp links.
        foreach (var (shared, hidden, owner) in new[] { ("S-A", "P-A", app.AdultA), ("S-B", "P-B", app.AdultB) })
        {
            uint version;
            await using (var scope = app.Services.CreateAsyncScope())
                version = (await scope.ServiceProvider.GetRequiredService<Nid.Goals.Application.IGoalStore>().FindAsync(goals[shared], default))!.Version;
            var (_, refused) = await app.InvokeAsync(owner, new Nid.Goals.Application.EditGoalCommand(goals[shared], Rows.Single(r => r.Name == shared).Label,
                10_000m, null, null, null, [new(envelopes[shared], 5000), new(envelopes[hidden], 4000)], version));
            if (refused is not null)
                throw new InvalidOperationException($"Linking {shared}'s goal failed: {refused.Code}");
        }

        // Through the store, as SeedPurchaseGrantingToAsync bypasses the pipeline: the subscription twins have no phase, so an
        // invoice would not validate. The pending rows are what the bench reads.
        var proposals = new Dictionary<string, IReadOnlyList<Guid>>();
        foreach (var (name, owner, label) in new[] { ("P-A", app.AdultA, PrivateLabelA), ("P-B", app.AdultB, PrivateLabelB) })
            proposals[name] = await SeedProposalsAsync(app, owner, label, envelopes[name], incomeSources[name], items[name], loans[name]);

        // Net worth projection spec §6: B saves the joint projection twin with an event on B's private loan twin (B sees it in « me »).
        // A reads that twin, so every read of it by A must leave the event out: the output guard and the scans check its bodies.
        var (_, jointRefused) = await app.InvokeAsync(app.AdultB, new Nid.Core.Application.Projection.ReplaceProjectionScenarioCommand(
            projectionScenarios["J-AB"], Rows.Single(r => r.Name == "J-AB").Label, Visibility.Shared, VisibilityScope.Me,
            RequestCatalogue.BenchProjection(RequestCatalogue.RepaymentEvent(loans["P-B"]))));
        if (jointRefused is not null)
            throw new InvalidOperationException($"Seeding J-AB's projection event failed: {jointRefused.Code}");

        // B's solo notice, its private PDF as source: only B reads it (the notices are the declarants' own).
        var noticeDocument = await DocumentApi.UploadedAsync(app, app.AdultB, DocumentApi.Pdf, "fileName=avis-2018.pdf");
        var noticeResponse = await app.RawAsync(app.AdultB, HttpMethod.Post, "/api/v1/tax/assessments", new
        {
            year = 2018,
            declarants = new[] { new { personId = app.AdultB.Value, rank = 1 } },
            referenceIncome = new { amount = NoticeFigureB, currency = "EUR" },
            documentId = noticeDocument,
        });
        if (noticeResponse.StatusCode != System.Net.HttpStatusCode.Created)
            throw new InvalidOperationException($"Seeding B's tax notice failed: {noticeResponse.StatusCode}");
        var notice = JsonDocument.Parse(await noticeResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        // One bank account of A's, named after A's private label and associated with A's private envelope twin: a banking read that showed it
        // to B would put the label in the textual scan.
        app.Banks.FailWith = null;
        app.Banks.Aspsps.Add(BankingApi.Alpha);
        await BankingApi.SettingsAsync(app, new { bankingEnabled = true, enableBankingAppId = "app-1" });
        // Her card too, attached to it: its lines are read through the checking account's envelope.
        // And a joint account, on the joint envelope twin, that both adults read and write.
        await BankingApi.ConnectAsync(app, app.AdultA, "bench", BankingApi.Account("uid-bench", "hash-bench", null, PrivateLabelA + " compte"),
            BankingApi.Account("uid-bench-card", "hash-bench-card", null, PrivateLabelA + " carte", "CARD"),
            BankingApi.Account("uid-bench-joint", "hash-bench-joint", null, "joint-ab compte"));
        var bankAccounts = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray()
            .ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("id").GetGuid());
        var bankAccount = bankAccounts[PrivateLabelA + " compte"];
        var bankCard = bankAccounts[PrivateLabelA + " carte"];
        var jointAccount = bankAccounts["joint-ab compte"];
        var associated = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{bankAccount}", new { envelopeId = envelopes["P-A"].Value });
        var attached = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{bankCard}", new { cardOfAccountId = bankAccount });
        var joint = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{jointAccount}", new { envelopeId = envelopes["J-AB"].Value });
        if (associated.StatusCode != System.Net.HttpStatusCode.OK || attached.StatusCode != System.Net.HttpStatusCode.OK || joint.StatusCode != System.Net.HttpStatusCode.OK)
            throw new InvalidOperationException($"Seeding A's bank accounts failed: {associated.StatusCode}, {attached.StatusCode}, {joint.StatusCode}");
        Guid jointLine;
        // Two booked lines of it, labelled with A's private label, for the transactions read.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<ITransactionStore>().AddAsync(bankAccount,
            [
                new NewBankTransaction("bench-1", new LocalDate(2025, 1, 3), null, -12.5m, Currency.Eur, PrivateLabelA + " ligne 1", null, null, "api"),
                new NewBankTransaction("bench-2", new LocalDate(2025, 1, 4), null, 40m, Currency.Eur, PrivateLabelA + " ligne 2", null, null, "api"),
            ], default);
            await scope.ServiceProvider.GetRequiredService<ITransactionStore>().AddAsync(bankCard,
                [new NewBankTransaction("bench-card-1", new LocalDate(2025, 1, 5), null, -7.2m, Currency.Eur, PrivateLabelA + " carte ligne", null, null, "api")], default);
            // D19: A's private subscription paid from the joint account, with A's rule; B reads the line as linked, without its name.
            var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
            await store.AddAsync(jointAccount,
                [new NewBankTransaction("bench-joint-1", new LocalDate(2025, 1, 6), null, -9.99m, Currency.Eur, "joint-ab ligne", null, null, "api")], default);
            jointLine = Assert.Single(await store.ListAsync([jointAccount], null, null, 0, 10, default)).Id;
            await scope.ServiceProvider.GetRequiredService<ILinkStore>().SetAsync(new BankLink(jointLine, "subscriptions", items["P-A"], "2025-01-06", null,
                LinkOutcome.AsExpected, null, $"person:{app.AdultA.Value:D}", Instant.FromUtc(2025, 1, 6, 10, 0)), TransactionLinkState.Linked, default);
            await scope.ServiceProvider.GetRequiredService<IRuleStore>().UpsertAsync(jointAccount, "JOINT-AB LIGNE", "subscriptions", items["P-A"], app.AdultA, default);
            // §0 n°1 the other way: A's private-account line, labelled with her private label, linked by her rule to the joint subscription.
            // B reads the subscription (its history too), never the line's label nor the rule's stem.
            var privateLine = (await store.ListAsync([bankAccount], null, null, 0, 10, default)).Single(t => t.ProviderRef == "bench-1").Id;
            await scope.ServiceProvider.GetRequiredService<ILinkStore>().SetAsync(new BankLink(privateLine, "subscriptions", items["J-AB"], "2025-01-03", null,
                LinkOutcome.AsExpected, null, $"person:{app.AdultA.Value:D}", Instant.FromUtc(2025, 1, 3, 10, 0)), TransactionLinkState.Linked, default);
            await scope.ServiceProvider.GetRequiredService<IRuleStore>().UpsertAsync(bankAccount, PrivateLabelA + " LIGNE", "subscriptions", items["J-AB"], app.AdultA, default);
        }

        return new MixedDataset(app, items, envelopes, incomeSources, loans, properties, scenarios, projectionScenarios, goals, proposals, documents, unlinkDocuments)
        {
            NoticeB = (notice, noticeDocument),
            JointLine = jointLine,
            PrivatePhases = privatePhases,
            PrivateOneOffs = privateOneOffs,
        };
    }

    private static async Task<IReadOnlyList<Guid>> SeedProposalsAsync(
        NidApp app, PersonId owner, string label, ItemId envelope, ItemId source, ItemId subscription, ItemId loan)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System; // audited rows need an actor (1F3a U4)
        var store = scope.ServiceProvider.GetRequiredService<IProposalStore>();
        var ids = new List<Guid>();
        foreach (var (module, kind, target, key, json) in new (string, string, ItemId, string, string)[]
                 {
                     ("investments", "investments.record_valuation", envelope, "2025-09-30",
                         $$"""{"asOf":"2025-09-30","amount":{"amount":"1500.00","currency":"EUR"},"note":"{{label}}"}"""),
                     ("investments", "investments.record_statement", envelope, "2025-09-29",
                         $$"""{"asOf":"2025-09-29","movements":[{"on":"2025-09-01","kind":"contribution","amount":"100.00","label":"{{label}}"}]}"""),
                     ("income", "income.change_pay", source, "Salaire:2025-09",
                         """{"paidOn":"2025-09-30","phaseLabel":"Salaire","net":{"amount":"1100.00","currency":"EUR"},"effectiveOn":"2025-09-01"}"""),
                     ("subscriptions", "subscriptions.change_price", subscription, "2025-09-30",
                         """{"chargedOn":"2025-09-30","amount":{"amount":"12.00","currency":"EUR"}}"""),
                     ("realestate", "realestate.import_amortization", loan, loan.Value.ToString(),
                         """{"rows":[{"dueDate":"2025-02-15","payment":"84.25","interest":"0.83","principal":"83.42","crd":"916.58"}]}"""),
                 })
            ids.Add((await store.SubmitAsync(new NewProposal(module, kind, target, owner, key, JsonDocument.Parse(json).RootElement.Clone(),
                1, [], "mcp", null, label), CancellationToken.None)).Proposal.Id);
        return ids;
    }

    /// <summary>The §2 name of an item, for a failure message that says which row went wrong.</summary>
    public string NameOf(ItemId item) => names.TryGetValue(item, out var name) ? name : item.ToString();

    /// <summary>The property twin of a loan twin's row; any other id as it is (an unknown id stays unknown).</summary>
    public ItemId PropertyTwinOf(ItemId loan)
        => Loans.Where(kv => kv.Value == loan).Select(kv => (ItemId?)Properties[kv.Key]).FirstOrDefault() ?? loan;

    /// <summary>The property twin of a subscription twin's row; any other id as it is (an unknown id stays unknown).</summary>
    public ItemId PropertyTwinOfSubscription(ItemId subscription)
        => Items.Where(kv => kv.Value == subscription).Select(kv => (ItemId?)Properties[kv.Key]).FirstOrDefault() ?? subscription;

    /// <summary>The envelope twin of a scenario or goal twin's row; any other id as it is (an unknown id stays unknown).</summary>
    public ItemId EnvelopeTwinOf(ItemId twin)
        => Scenarios.Concat(Goals).Where(kv => kv.Value == twin).Select(kv => (ItemId?)Envelopes[kv.Key]).FirstOrDefault() ?? twin;

    /// <summary>The purchase scenario twin of a projection twin's row; any other id as it is (an unknown id stays unknown).</summary>
    public ItemId PurchaseTwinOf(ItemId twin)
        => ProjectionScenarios.Where(kv => kv.Value == twin).Select(kv => (ItemId?)Scenarios[kv.Key]).FirstOrDefault() ?? twin;

    /// <summary>Who uploaded a row's documents, by the row's subscription: its first adult owner, A for D's row.</summary>
    public PersonId UploaderOf(ItemId item)
        => Rows.Single(r => Items[r.Name] == item).Owners.Select(o => PersonOf(App, o)).FirstOrDefault(p => p == App.AdultA || p == App.AdultB, App.AdultA);

    /// <summary>The §2 visibility of a twin's row.</summary>
    public Visibility VisibilityOf(ItemId twin)
        => Rows.Single(r => r.Name == NameOf(twin).Split(' ')[0]).Visibility;

    /// <summary>"A", "B" or "D" — how access-tests.md §2 names this person.</summary>
    public string NameOf(PersonId person)
        => person == App.AdultA ? "A" : person == App.AdultB ? "B" : person == App.Dependent ? "D" : person.ToString();

    /// <summary>
    /// The items each adult may read, read off the table of access-tests.md §2: a row's subscription and its
    /// envelope and income twins alike.
    /// </summary>
    public IReadOnlySet<ItemId> ExpectedReadable(PersonId actor)
        => Rows.Where(r => r.Readers.Contains(NameOf(actor)))
            .SelectMany(r => new[]
            {
                Items[r.Name], Envelopes[r.Name], IncomeSources[r.Name], Loans[r.Name], Properties[r.Name], Scenarios[r.Name], ProjectionScenarios[r.Name],
                Goals[r.Name],
            })
            .ToHashSet();

    /// <summary>The §2 level of a twin's row.</summary>
    public ShareLevel ShareLevelOf(ItemId twin)
        => Rows.Single(r => r.Name == NameOf(twin).Split(' ')[0]).Level;

    /// <summary>#294: whether access-tests.md §2 makes this actor an owner of this row — or every owner is a dependent.</summary>
    public bool ExpectedOwned(string name, PersonId actor)
        => Rows.Single(r => r.Name == name) is var row && (row.Owners.Contains(NameOf(actor)) || row.Owners.All(o => o == "D"));

    /// <summary>Whether access-tests.md §2 says this actor may change this item.</summary>
    public bool ExpectedWritable(string name, PersonId actor)
        => Rows.Single(r => r.Name == name).Writers.Contains(NameOf(actor));

    /// <summary>
    /// What a list in <paramref name="scope"/> must return, exactly: the shared items, plus — in
    /// <see cref="VisibilityScope.Me"/> — the actor's own ones (<c>Visibility.cs</c>). Asserting equality
    /// rather than containment catches an item that leaks AND one that wrongly disappears.
    /// </summary>
    /// <paramref name="of"/> is <see cref="Items"/>, <see cref="Envelopes"/> or <see cref="IncomeSources"/>: which of a row's twins to list.
    public IReadOnlySet<ItemId> ExpectedVisible(PersonId actor, VisibilityScope scope, IReadOnlyDictionary<string, ItemId>? of = null)
        => Rows.Where(r => r.Visibility == Visibility.Shared
                           || (scope == VisibilityScope.Me && r.Owners.Contains(NameOf(actor))))
            .Select(r => (of ?? Items)[r.Name])
            .ToHashSet();

    /// <summary>The private document of the adult who is not <paramref name="actor"/>, cited by that adult's shared envelope and property twins.</summary>
    public Guid ForbiddenDocument(PersonId actor) => Documents[Items[actor == App.AdultA ? "P-B" : "P-A"]];

    /// <summary>The label of the private item of the adult who is not <paramref name="actor"/>.</summary>
    public string ForbiddenLabel(PersonId actor) => actor == App.AdultA ? PrivateLabelB : PrivateLabelA;

    private static PersonId PersonOf(NidApp app, string name) => name switch
    {
        "A" => app.AdultA,
        "B" => app.AdultB,
        "D" => app.Dependent,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "access-tests.md §2 knows A, B and D."),
    };
}

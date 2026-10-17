using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Investments.Application;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Application;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.LeakBench;

/// <summary>
/// The dataset of access-tests.md §2: adults A and B, dependent D, and one item of every ownership and
/// visibility shape. Labels are distinctive so a leak can be caught by searching the serialised response.
/// </summary>
public sealed class MixedDataset
{
    public const string PrivateLabelA = "ZZPRIVATEALPHA";
    public const string PrivateLabelB = "ZZPRIVATEBETA";

    /// <summary>
    /// One row of the table in access-tests.md §2, transcribed. <see cref="Readers"/> and
    /// <see cref="Writers"/> are the columns of that table — the specification — and are NOT computed by
    /// calling <c>AccessRules</c>, so the bench compares the running system against the document rather
    /// than against itself. <c>AccessRulesPropertyTests</c> checks the rules themselves.
    /// </summary>
    private sealed record Row(
        string Name, string Label, Visibility Visibility, string[] Owners, string[] Readers, string[] Writers);

    private static readonly Row[] Rows =
    [
        new("S-A", "shared-a", Visibility.Shared, ["A"], ["A", "B"], ["A"]),
        new("S-B", "shared-b", Visibility.Shared, ["B"], ["A", "B"], ["B"]),
        new("J-AB", "joint-ab", Visibility.Shared, ["A", "B"], ["A", "B"], ["A", "B"]),
        new("P-A", PrivateLabelA, Visibility.Private, ["A"], ["A"], ["A"]),
        new("P-B", PrivateLabelB, Visibility.Private, ["B"], ["B"], ["B"]),
        new("D", "dependent-owned", Visibility.Shared, ["D"], ["A", "B"], ["A", "B"]),
        new("M-AD", "mixed-ad", Visibility.Shared, ["A", "D"], ["A", "B"], ["A", "B"]),
    ];

    private readonly Dictionary<ItemId, string> names;

    private MixedDataset(
        NidApp app, IReadOnlyDictionary<string, ItemId> items, IReadOnlyDictionary<string, ItemId> envelopes,
        IReadOnlyDictionary<string, ItemId> incomeSources, IReadOnlyDictionary<string, ItemId> loans,
        IReadOnlyDictionary<string, ItemId> properties, IReadOnlyDictionary<string, IReadOnlyList<Guid>> proposals)
    {
        (App, Items, Envelopes, IncomeSources, Loans, Properties, Proposals) = (app, items, envelopes, incomeSources, loans, properties, proposals);
        names = items.ToDictionary(kv => kv.Value, kv => kv.Key);
        foreach (var (name, envelope) in envelopes)
            names[envelope] = name + " (envelope)";
        foreach (var (name, source) in incomeSources)
            names[source] = name + " (income)";
        foreach (var (name, loan) in loans)
            names[loan] = name + " (loan)";
        foreach (var (name, property) in properties)
            names[property] = name + " (property)";
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

    /// <summary>
    /// 4D1 D9: one pending proposal of each kind (valuation, statement, payslip, invoice, amortisation table) on each private row's twins, keyed
    /// "P-A" and "P-B", their texts carrying the row's label: the textual scan catches one shown to the other adult.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Guid>> Proposals { get; }

    public ItemId this[string name] => Items[name];

    /// <summary>
    /// The rows a request is run against: the envelope twins for a request of <c>Nid.Investments.Application</c>
    /// (a subscription is not an envelope, so it would only ever be refused on its own merits), the income twins for
    /// one of <c>Nid.Income.Application</c>, the subscriptions for every other one.
    /// </summary>
    public IReadOnlyDictionary<string, ItemId> ItemsFor(Type requestType)
        => requestType.Assembly == typeof(Nid.Investments.Application.ListEnvelopesQuery).Assembly ? Envelopes
            : requestType.Assembly == typeof(Nid.Income.Application.ListIncomeSourcesQuery).Assembly ? IncomeSources
            : requestType.Assembly == typeof(Nid.RealEstate.Application.ListLoansQuery).Assembly
                // Every property request carries « Propert » in its name (ListPropertiesQuery, RecordPropertyValuationCommand…).
                ? (requestType.Name.Contains("Propert", StringComparison.Ordinal) ? Properties : Loans)
            : Items;

    public static async Task<MixedDataset> CreateAsync(PostgresFixture fixture)
    {
        var app = await NidApp.StartAsync(fixture);

        var items = new Dictionary<string, ItemId>();
        var envelopes = new Dictionary<string, ItemId>();
        var incomeSources = new Dictionary<string, ItemId>();
        var loans = new Dictionary<string, ItemId>();
        var properties = new Dictionary<string, ItemId>();
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

        // Through the store, as SeedPurchaseGrantingToAsync bypasses the pipeline: the subscription twins have no phase, so an
        // invoice would not validate. The pending rows are what the bench reads.
        var proposals = new Dictionary<string, IReadOnlyList<Guid>>();
        foreach (var (name, owner, label) in new[] { ("P-A", app.AdultA, PrivateLabelA), ("P-B", app.AdultB, PrivateLabelB) })
            proposals[name] = await SeedProposalsAsync(app, owner, label, envelopes[name], incomeSources[name], items[name], loans[name]);

        return new MixedDataset(app, items, envelopes, incomeSources, loans, properties, proposals);
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

    /// <summary>"A", "B" or "D" — how access-tests.md §2 names this person.</summary>
    public string NameOf(PersonId person)
        => person == App.AdultA ? "A" : person == App.AdultB ? "B" : person == App.Dependent ? "D" : person.ToString();

    /// <summary>
    /// The items each adult may read, read off the table of access-tests.md §2: a row's subscription and its
    /// envelope and income twins alike.
    /// </summary>
    public IReadOnlySet<ItemId> ExpectedReadable(PersonId actor)
        => Rows.Where(r => r.Readers.Contains(NameOf(actor)))
            .SelectMany(r => new[] { Items[r.Name], Envelopes[r.Name], IncomeSources[r.Name], Loans[r.Name], Properties[r.Name] })
            .ToHashSet();

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

using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using Nid.Documents.Contracts;
using Nid.Documents.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Documents.Application;

/// <summary>
/// Issue #414: a document a subject still lacks. <c>Subject</c> names its rule (<see cref="ExpectedDocumentRules"/>); the target is the
/// item, or the actor's <c>core.person</c> for the tax notice. <c>Period</c> is the calendar year, or 0 for a document expected once.
/// </summary>
public sealed record ExpectedDocumentView(string Subject, string TargetKind, Guid TargetId, string Label, string? Module, string Role, int Period);

public sealed record ExpectedDocumentList(IReadOnlyList<ExpectedDocumentView> Items) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.Where(i => i.TargetKind == TargetKinds.CoreItem).Select(i => new ItemId(i.TargetId));
}

/// <summary>
/// Issue #414: the documents missing on the active items the actor writes (one item with <paramref name="Item"/>), and the actor's tax
/// notice when no item is named. Only what the actor can file or dismiss is listed.
/// </summary>
public sealed record ListExpectedDocumentsQuery(Guid? Item = null) : IQuery<ExpectedDocumentList>, IOptionalItemScoped
{
    public ItemId? ItemId => Item is { } item ? new ItemId(item) : null;

    public AccessKind Access => AccessKind.Write;
}

/// <summary>Issue #414: « Pas disponible »: the document of that period is no longer asked for. Only a listed one may be dismissed.</summary>
public sealed record DismissExpectedDocumentCommand(DocumentTarget Target, string Role, int Period) : ICommand<Guid>, IOptionalItemScoped
{
    public ItemId? ItemId => Target.Kind == TargetKinds.CoreItem ? new ItemId(Target.Id) : null;

    public AccessKind Access => AccessKind.Write;
}

public sealed record ExpectedDismissal(string TargetKind, Guid TargetId, string Role, int Period, Guid DismissedBy, Instant DismissedAt);

/// <summary>Issue #414: applies <see cref="ExpectedDocumentRules"/> to the actor's subjects.</summary>
public sealed class ExpectedDocuments(
    IVisibleItems visible, IItemRegistry items, IAccessPolicy policy, IEnumerable<IExpectedDocumentSubjects> modules, IPersonRegistry persons,
    ITaxAssessmentDeclarants assessments, IDocumentStore store, IHouseholdCalendar calendar)
{
    private sealed record Subject(string Kind, DocumentTarget Target, string Label, string? Module, LocalDate? Since);

    public async Task<IReadOnlyList<ExpectedDocumentView>> ListAsync(PersonId person, Guid? item, CancellationToken ct)
    {
        var ids = item is { } one ? [new ItemId(one)] : (await visible.ForAsync(person, VisibilityScope.Me, ct)).ToList();
        var writable = await policy.CheckManyAsync(person, ids, AccessKind.Write, ct);
        var found = (await items.FindManyAsync(writable.ToList(), ct)).Values.Where(i => i.ArchivedAt is null).ToList();
        // #511: each module names its own items' subjects (Investments its open life insurances, RealEstate its loans and properties).
        var expectedOf = new Dictionary<ItemId, ExpectedSubject>();
        foreach (var module in modules)
            foreach (var (id, subject) in await module.SubjectsAsync(found, ct))
                expectedOf[id] = subject;

        var subjects = new List<Subject>();
        foreach (var i in found)
            if (expectedOf.TryGetValue(i.Id, out var s))
                subjects.Add(new Subject(s.Kind, new DocumentTarget(TargetKinds.CoreItem, i.Id.Value), i.DisplayName, i.Module, s.Since));
        if (item is null && await persons.FindAsync(person, ct) is { } me)
            subjects.Add(new Subject(ExpectedDocumentRules.TaxYear, new DocumentTarget(TargetKinds.CorePerson, person.Value), me.DisplayName, null, null));
        if (subjects.Count == 0)
            return [];

        // ponytail: reads every document and link, as the list does; a query by anchor and role when the household holds thousands.
        var documents = (await store.ListAsync(includeArchived: true, ct)).ToDictionary(d => d.Id);
        var links = await store.LinksOfAsync(documents.Keys.ToList(), ct);
        var dismissed = (await store.DismissalsAsync(ct)).ToLookup(d => (d.TargetKind, d.TargetId, d.Role), d => d.Period);
        // A saved notice with its PDF covers the year it was issued, whatever the PDF's date.
        var notices = await assessments.IssuedYearsWithDocumentAsync(person, ct);
        var today = await calendar.TodayAsync(ct);

        var expected = new List<ExpectedDocumentView>();
        foreach (var s in subjects)
        {
            var rule = ExpectedDocumentRules.BySubject[s.Kind];
            var held = links.Where(l => l.Role == rule.Role && (s.Target.Kind == TargetKinds.CoreItem
                    ? l.AnchorItemId == s.Target.Id
                    : l.TargetKind == s.Target.Kind && l.TargetId == s.Target.Id))
                .Select(l => documents[l.DocumentId]);
            var covered = rule.Due is not { } due
                ? held.Select(_ => ExpectedDocumentRules.Once).ToHashSet()
                : held.Select(d => due.PeriodOf(d.DocumentDate, d.UploadedAt.InUtc().Date)).ToHashSet();
            if (s.Kind == ExpectedDocumentRules.TaxYear)
                covered.UnionWith(notices);
            expected.AddRange(ExpectedDocumentRules.Missing(rule, s.Since, today, covered, dismissed[(s.Target.Kind, s.Target.Id, rule.Role)].ToHashSet())
                .Select(period => new ExpectedDocumentView(s.Kind, s.Target.Kind, s.Target.Id, s.Label, s.Module, rule.Role, period)));
        }
        return expected;
    }
}

[RequestHandler<ListExpectedDocumentsQuery, ExpectedDocumentList>]
public sealed class ListExpectedDocumentsHandler(ExpectedDocuments expected, IActorAccessor actor)
    : IRequestHandler<ListExpectedDocumentsQuery, ExpectedDocumentList>
{
    public async ValueTask<Result<ExpectedDocumentList>> HandleAsync(ListExpectedDocumentsQuery query, CancellationToken ct = default)
        => Result.Success(new ExpectedDocumentList(await expected.ListAsync(actor.Current!.PersonId!.Value, query.Item, ct)));
}

[RequestHandler<DismissExpectedDocumentCommand, Guid>]
public sealed class DismissExpectedDocumentHandler(ExpectedDocuments expected, IDocumentStore store, IActorAccessor actor, IClock clock)
    : IRequestHandler<DismissExpectedDocumentCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(DismissExpectedDocumentCommand command, CancellationToken ct = default)
    {
        var person = actor.Current!.PersonId!.Value;
        var target = command.Target;
        // The actor's own list decides: another adult's tax notice, an unknown item or a document already held are all « not found ».
        var listed = await expected.ListAsync(person, target.Kind == TargetKinds.CoreItem ? target.Id : null, ct);
        if (!listed.Any(e => e.TargetKind == target.Kind && e.TargetId == target.Id && e.Role == command.Role && e.Period == command.Period))
            return Result.Failure<Guid>(new NotFoundFailure("expected document", $"{target.Kind}/{target.Id}/{command.Role}/{command.Period}"));

        await store.DismissAsync(new ExpectedDismissal(target.Kind, target.Id, command.Role, command.Period, person.Value, clock.GetCurrentInstant()), ct);
        return Result.Success(target.Id);
    }
}

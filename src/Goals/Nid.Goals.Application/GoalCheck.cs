using Nid.Core.Contracts;
using Nid.Goals.Contracts;
using Nid.Goals.Domain;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Goals.Application;

/// <summary>Savings goals spec §2, D10, D10c, D12: the checks the goal writes share.</summary>
internal static class GoalCheck
{
    public const int NameMax = 100;
    public const int NoteMax = 2000;
    public const int DefaultMonths = 6;
    private const decimal AmountMax = 1_000_000_000_000_000m; // numeric(19,4)

    /// <summary>The goal and its item; an item that is not a goal answers as a missing goal.</summary>
    public static async Task<Result<(Goal Goal, ItemAccess Item)>> LoadAsync(IGoalStore goals, IItemRegistry items, ItemId id, CancellationToken ct)
    {
        var goal = await goals.FindAsync(id, ct);
        var item = goal is null ? null : await items.FindAsync(id, ct);
        return goal is null || item is null
            ? Result.Failure<(Goal, ItemAccess)>(new NotFoundFailure("goal", id.ToString()))
            : Result.Success((goal, item));
    }

    /// <summary>D12: a closed goal refuses every write but its reopening.</summary>
    public static Result RequireOpen(ItemAccess item)
        => item.ArchivedAt is null ? Result.Success() : Fail(GoalsCodes.GoalClosed, "This goal is closed: reopen it first.", null);

    /// <summary>Spec §2: an emergency fund covers 6 months when not told otherwise.</summary>
    public static int? Months(string? kind, int? months) => kind == GoalKinds.EmergencyFund ? months ?? DefaultMonths : months;

    /// <summary>
    /// The editable fields, by kind (spec §2), <paramref name="months"/> as <see cref="Months"/> gives them. <paramref name="storedDate"/>:
    /// an edit keeps a past target date it already had.
    /// </summary>
    public static Result Fields(
        string? name, string kind, decimal? target, int? months, LocalDate? targetDate, string? note, LocalDate today, LocalDate? storedDate = null)
    {
        if (name is null || name.Trim().Length is < 1 or > NameMax)
            return Field("name", $"A name is 1 to {NameMax} characters, and not only whitespace.");
        if (note is { Length: > NoteMax })
            return Field("note", $"A note has at most {NoteMax} characters.");
        if (!GoalKinds.All.Contains(kind))
            return Fail(GoalsCodes.UnknownKind, $"'{kind}' is not a goal kind: savings or emergency_fund.", "kind");
        if (kind == GoalKinds.Savings)
        {
            if (target is not > 0)
                return Fail(GoalsCodes.InvalidTarget, "A savings goal has a target amount above zero.", "targetAmount");
            if (Amount(target, "targetAmount").TryGetFailure(out var tooLarge))
                return Result.Failure(tooLarge!);
            if (months is not null)
                return Fail(GoalsCodes.InvalidTarget, "A savings goal has a target amount, not months.", "months");
        }
        else
        {
            if (target is not null)
                return Fail(GoalsCodes.InvalidTarget, "An emergency fund's target follows the commitments: give months, not an amount.", "targetAmount");
            if (months is < 1 or > 24)
                return Fail(GoalsCodes.InvalidMonths, "An emergency fund covers 1 to 24 months.", "months");
        }
        if (targetDate is { } due && due < today && due != storedDate)
            return Fail(GoalsCodes.TargetDatePast, "A target date is not in the past.", "targetDate");
        return Result.Success();
    }

    /// <summary>At most 4 decimals and 15 digits before the point: what the column holds (a target, an amount reached).</summary>
    public static Result Amount(decimal? amount, string field)
        => amount is not { } a || (decimal.Round(a, 4) == a && Math.Abs(a) < AmountMax)
            ? Result.Success()
            : Fail(GoalsCodes.InvalidTarget, "An amount has at most 4 decimals and fits 15 digits.", field);

    /// <summary>D2: each share 1 to 10 000 basis points, each envelope once.</summary>
    public static Result Shares(IReadOnlyList<FundingShare> funding)
    {
        for (var i = 0; i < funding.Count; i++)
        {
            if (funding[i].ShareBp is < 1 or > 10_000)
                return Fail(GoalsCodes.InvalidShare, "A share is 1 to 10000 basis points.", $"funding[{i}].shareBp");
            if (funding.Take(i).Any(f => f.Envelope == funding[i].Envelope))
                return Fail(GoalsCodes.InvalidShare, "An envelope funds a goal once.", $"funding[{i}].envelopeId");
        }
        return Result.Success();
    }

    /// <summary>
    /// D10: an envelope the actor links is one they see (scope <c>me</c>) and that is open, unless it is already linked to this
    /// goal (<paramref name="kept"/>: a closed envelope's link stays until it is removed, D13). An envelope they do not see answers
    /// as one that does not exist.
    /// </summary>
    public static Result Linkable(IReadOnlyList<FundingShare> funding, IReadOnlyDictionary<ItemId, EnvelopeFundingFigures> seen, IReadOnlyCollection<ItemId> kept)
    {
        for (var i = 0; i < funding.Count; i++)
            if (!seen.TryGetValue(funding[i].Envelope, out var envelope) || (envelope.Closed && !kept.Contains(funding[i].Envelope)))
                return Fail(GoalsCodes.EnvelopeNotVisible, "This envelope cannot be linked: it is unknown or closed.", $"funding[{i}].envelopeId");
        return Result.Success();
    }

    /// <summary>The envelopes among <paramref name="ids"/> the actor sees in the scope <c>me</c>, closed ones included.</summary>
    public static async Task<Result<IReadOnlyDictionary<ItemId, EnvelopeFundingFigures>>> SeenAsync(
        IEnvelopeFunding envelopes, PersonId actor, IReadOnlyCollection<ItemId> ids, LocalDate today, CancellationToken ct)
    {
        if (ids.Count == 0)
            return Result.Success<IReadOnlyDictionary<ItemId, EnvelopeFundingFigures>>(new Dictionary<ItemId, EnvelopeFundingFigures>());
        var read = await envelopes.ReadAsync(actor, VisibilityScope.Me, ids.Distinct().ToList(), today, ct);
        if (!read.TryGetValue(out var figures))
            return Result.Failure<IReadOnlyDictionary<ItemId, EnvelopeFundingFigures>>(read.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        return Result.Success<IReadOnlyDictionary<ItemId, EnvelopeFundingFigures>>(figures!.ToDictionary(f => f.Envelope));
    }

    /// <summary>
    /// D10: on each open envelope of <paramref name="funding"/>, the shares of the other open goals the actor sees plus this one stay
    /// within 10 000. A goal the actor does not see neither counts nor shows: the free share is the one they can see.
    /// </summary>
    /// <paramref name="located"/>: the links are the body's <c>funding</c>, so the refusal names the share; a reopening sends none.
    /// <paramref name="held"/>: an edit's stored links (none otherwise); one kept at the same share or lower is not checked again, so a state D10 lets
    /// a reader see over 100 % (another adult's goal they did not see) never locks an edit out.
    public static async Task<Result> RoomAsync(
        IGoalStore goals, IVisibleItems visible, PersonId actor, ItemId? self, IReadOnlyList<FundingShare> funding,
        IReadOnlyDictionary<ItemId, EnvelopeFundingFigures> seen, bool located, IReadOnlyList<Funding> held, CancellationToken ct)
    {
        var checkedLinks = funding.Select((f, i) => (Link: f, Index: i))
            .Where(l => !seen[l.Link.Envelope].Closed && !held.Any(h => h.Envelope == l.Link.Envelope && h.ShareBp >= l.Link.ShareBp))
            .ToList();
        if (checkedLinks.Count == 0)
            return Result.Success();
        var openGoals = await visible.ForAsync(actor, VisibilityScope.Me, ct); // active items only: the open goals
        var others = (await goals.FundingOnAsync(checkedLinks.Select(l => l.Link.Envelope).ToList(), ct))
            .Where(f => f.Goal != self && openGoals.Contains(f.Goal))
            .ToLookup(f => f.Envelope, f => f.ShareBp);
        foreach (var (link, i) in checkedLinks)
        {
            if (others[link.Envelope].Sum() + link.ShareBp <= 10_000)
                continue;
            var metadata = new Dictionary<string, object?> { ["freeBp"] = GoalMath.FreeShareBp(others[link.Envelope]) };
            if (located)
                metadata["field"] = $"funding[{i}].shareBp";
            return Result.Failure(new Failure(GoalsCodes.ShareExceeded, "The goals on this envelope would share more than all of it.", metadata));
        }
        return Result.Success();
    }

    private static Result Field(string path, string message)
        => Result.Failure(FieldErrors.Shape([new FieldError(path, FieldErrors.ShapeCode, message)]));

    private static Result Fail(string code, string message, string? field)
        => Result.Failure(new Failure(code, message, field is null ? null : new Dictionary<string, object?> { ["field"] = field }));
}

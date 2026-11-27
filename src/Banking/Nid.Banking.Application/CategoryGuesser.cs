using Nid.Ai.Contracts;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.Application;

/// <summary>
/// #450 (<c>docs/ai/README.md</c> §7.2), in <c>ModelWorkLoop</c>: asks the <c>decide</c> use's model, once its profile offers it, the category of
/// each open pattern nothing else decides, and keeps the answer for the household (<c>banking.category_guess</c>), per model and digest. What
/// goes is masked (<see cref="BankLabelMask"/>): the pattern, its newest label, its counterparty, the direction and the categories of that
/// side; a pattern holding a household name never goes, nor a line paid to or by a member. Each request concerns the adults who read the
/// accounts of its lines. Shown only to a reader of those lines, on their own open lines (<see cref="CategoryPicks"/>).
/// </summary>
public sealed class CategoryGuesser(
    IDecisions decisions, DecideSetup setup, IDecideCalibrationStore calibrations, ICategorySuggestionStore suggestions, ICategoryStore categories,
    IAccountStore accounts, IPersonRegistry persons, BankingAccess access, IClock clock)
{
    /// <summary>Batches asked a pass, each in two steps (<see cref="CategoryQuestion"/>).</summary>
    private const int RequestsPerPass = 4;

    // ponytail: the newest open lines only; a household with more waits for its oldest patterns until those are classified.
    private const int Lines = 10_000;

    /// <summary>The last option of every request: a pattern none of the categories fits.</summary>
    public const string NoneOfThese = "Aucune de ces catégories";

    /// <summary>The text a model reads for a pattern, masked, within the request's bounds.</summary>
    public static string Text(string pattern, string label, string? counterparty, bool debit, IReadOnlyCollection<string> names)
    {
        static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
        var lines = new List<string>
        {
            $"Sens : {(debit ? "débit" : "crédit")}",
            $"Motif : {Cut(BankLabelMask.Mask(pattern, names), 80)}",
            $"Libellé : {Cut(BankLabelMask.Mask(label, names), 160)}",
        };
        if (counterparty is { Length: > 0 })
            lines.Add($"Contrepartie : {Cut(BankLabelMask.Mask(counterparty, names), 100)}");
        return string.Join('\n', lines);
    }

    /// <summary>A pattern to ask about: its text, and the adults whose allow it needs to leave the premises.</summary>
    public sealed record Pending(string Pattern, bool Debit, string Text, IReadOnlyList<Guid> Concerned);

    /// <summary>
    /// The patterns of <paramref name="lines"/> (newest first) to ask about, in that order: not decided by the household's own choices
    /// (<paramref name="decided"/>), no household name in the pattern, the lines paid to or by a member left out; the newest line left speaks for
    /// the pattern, and its accounts' <paramref name="readers"/> are concerned.
    /// </summary>
    public static IReadOnlyList<Pending> Collect(IReadOnlyList<GuessableLine> lines, IReadOnlySet<(string Pattern, bool Debit)> decided,
        IReadOnlyCollection<string> names, IReadOnlyDictionary<Guid, IReadOnlyList<PersonId>> readers)
        => [.. lines.Where(l => !decided.Contains((l.Pattern, l.Debit)) && !Named(l.Pattern, names) && (l.Counterparty is null || !Named(l.Counterparty, names)))
            .GroupBy(l => (l.Pattern, l.Debit))
            .Select(g => new Pending(g.Key.Pattern, g.Key.Debit, Text(g.Key.Pattern, g.First().Label, g.First().Counterparty, g.Key.Debit, names),
                [.. g.SelectMany(l => readers.GetValueOrDefault(l.Account) ?? []).Select(p => p.Value).Distinct().Order()]))];

    private static bool Named(string text, IReadOnlyCollection<string> names) => BankLabelMask.Mask(text, names).Contains(BankLabelMask.Name, StringComparison.Ordinal);

    /// <summary>The household's categories of each side in use, masked, as the two steps offer them (<see cref="CategoryQuestion.Tree"/>); null
    /// for a side with too many to ask.</summary>
    public static IReadOnlyDictionary<bool, IReadOnlyList<CategoryQuestion.Top>?> Options(IReadOnlyList<BankCategory> all, IReadOnlyCollection<string> names)
    {
        IReadOnlyList<CategoryQuestion.Top>? Side(CategoryKind kind)
            => CategoryQuestion.Tree([.. all.Where(c => c.Kind == kind && c.ArchivedAt is null).Select(c => (c.Id, c.Parent, BankLabelMask.Mask(c.Name, names)))]);
        return new Dictionary<bool, IReadOnlyList<CategoryQuestion.Top>?> { [true] = Side(CategoryKind.Expense), [false] = Side(CategoryKind.Income) };
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (await setup.WorkerProfileAsync(calibrations, ct) is not { } found)
            return;
        var (key, profile) = found;
        var people = await persons.ListAsync(ct);
        var names = CategorySuggestions.NameWords(people.Select(p => p.DisplayName));
        IReadOnlyList<Guid> adults = [.. people.Where(p => p.Kind == PersonKind.Adult).Select(p => p.Id.Value).Order()];
        var all = await accounts.AllAsync(ct);
        var ids = all.Select(a => a.Id).ToList();
        var categoryList = await categories.AllAsync(ct);
        var inUse = categoryList.Where(c => c.ArchivedAt is null).Select(c => c.Id).ToHashSet();
        var decided = CategorySuggestions.Decide(await suggestions.EvidenceAsync(ids, ct), inUse.Contains).Select(p => (p.Pattern, p.Debit)).ToHashSet();
        // Lines no signed-in adult reads concern every adult, as the pattern index's do: an empty list would need nobody's allow.
        var pending = Collect(await suggestions.GuessableAsync(ids, key, Lines, ct), decided, names, await access.ReadersAsync(all, ct))
            .Select(p => p.Concerned.Count > 0 ? p : p with { Concerned = adults });
        var options = Options(categoryList, names);

        var sent = 0;
        foreach (var batch in pending.GroupBy(p => (p.Debit, Concerned: string.Join(',', p.Concerned))).SelectMany(g => g.Chunk(CategoryQuestion.Batch)))
        {
            if (sent == RequestsPerPass)
                break;
            if (options[batch[0].Debit] is not { } side)
                continue; // more categories than one list offers: that side is not asked
            var asked = await CategoryQuestion.AskAsync(decisions, [.. batch.Select(p => p.Text)], batch[0].Debit, side, DataClasses.BankLabels,
                batch[0].Concerned, ct);
            if (!asked.TryGetValue(out var answer))
            {
                // An adult who has not allowed it holds back their own lines only; anything else stops the pass, the next one tries again.
                if (asked.TryGetFailure(out var failure) && failure!.Code == JobCodes.OffsiteNotAllowed)
                    continue;
                return;
            }
            sent++;
            if (answer.Key != key)
                return;
            var at = clock.GetCurrentInstant();
            await suggestions.SaveGuessesAsync([.. batch.Zip(answer.Guesses, (p, g) =>
            {
                var (category, probability) = Read(g, profile);
                return new CategoryGuess(p.Pattern, p.Debit, category, probability, key, answer.Placement, at);
            })], ct);
        }
    }

    /// <summary>With scores, the category picked and its probability at the profile's temperature (<see cref="DecideCalibration.Pick"/>);
    /// without, the option written at the last step.</summary>
    public static (Guid? Category, double? Probability) Read(DecideCalibration.Guess guess, DecideProfile profile)
    {
        if (profile.Scoreless)
            return (guess.Written, null);
        return DecideCalibration.Pick(guess, profile.Temperature ?? 1) is { } pick ? (pick.Option, pick.Probability) : (null, null);
    }
}

/// <summary>
/// #450: asks <c>ModelWorkLoop</c> for a guess pass at its next tick rather than at the next 5 minutes: once a calibration is done, or
/// « Suggérer à nouveau ». One per host (a singleton); a host that restarts runs a pass at its first tick anyway.
/// </summary>
public sealed class CategoryGuessSignal
{
    private int _requested;

    public void Request() => Interlocked.Exchange(ref _requested, 1);

    /// <summary>True once per <see cref="Request"/>, however many came before.</summary>
    public bool TryTake() => Interlocked.Exchange(ref _requested, 0) == 1;
}

/// <summary>#450, « Suggérer à nouveau » (an adult): every guess goes, and a pass asks the model again within seconds.</summary>
public sealed record RefreshCategoryGuessesCommand : ICommand<int>, IRequiresBanking;

/// <summary>#450 (an adult): up to three texts the guess job would send, masked, taken from lines of accounts the reader reads.</summary>
public sealed record GetCategoryGuessSampleQuery : IQuery<CategoryGuessSampleView>, IRequiresBanking;

/// <summary>What the model reads: each <paramref name="Texts"/> with the categories of its side as options, and nothing else (no amount,
/// no date, no account).</summary>
public sealed record CategoryGuessSampleView(IReadOnlyList<string> Texts);

[RequestHandler<RefreshCategoryGuessesCommand, int>]
public sealed class RefreshCategoryGuessesHandler(ICategorySuggestionStore suggestions, CategoryGuessSignal signal)
    : IRequestHandler<RefreshCategoryGuessesCommand, int>
{
    public async ValueTask<Result<int>> HandleAsync(RefreshCategoryGuessesCommand command, CancellationToken ct = default)
    {
        var forgotten = await suggestions.ForgetGuessesAsync(ct);
        signal.Request();
        return Result.Success(forgotten);
    }
}

[RequestHandler<GetCategoryGuessSampleQuery, CategoryGuessSampleView>]
public sealed class GetCategoryGuessSampleHandler(BankingAccess access, IAccountStore accounts, ICategorySuggestionStore suggestions, IPersonRegistry persons)
    : IRequestHandler<GetCategoryGuessSampleQuery, CategoryGuessSampleView>
{
    public const int Size = 3;

    public async ValueTask<Result<CategoryGuessSampleView>> HandleAsync(GetCategoryGuessSampleQuery query, CancellationToken ct = default)
    {
        var readable = (await access.ReadableAsync(await accounts.AllAsync(ct), ct)).Select(r => r.Account.Id).ToList();
        var names = CategorySuggestions.NameWords((await persons.ListAsync(ct)).Select(p => p.DisplayName));
        var lines = readable.Count == 0 ? [] : await suggestions.GuessableAsync(readable, null, 200, ct);
        return Result.Success(new CategoryGuessSampleView([.. CategoryGuesser.Collect(lines, new HashSet<(string, bool)>(), names,
            new Dictionary<Guid, IReadOnlyList<PersonId>>()).Take(Size).Select(p => p.Text)]));
    }
}

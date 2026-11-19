using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Banking.Application.Reconciliation;

/// <summary>
/// Transaction categories spec §Hints and the assistant: `{ lines: [{ transactionId, categoryId }] }`, 1 to 100 distinct lines, no target
/// (its submitter alone reads and decides it). Submitted, every category is in use and every line one the submitter reads. Approved, each
/// line the approver writes and whose category was not chosen by a person (hand, assistant) takes its category as `assistant`; the others are
/// skipped. Not editable: a wrong guess is rejected and the lines categorised by hand.
/// </summary>
public sealed class CategoriseProposal(
    BankingAccess access, IAccountStore accounts, ITransactionStore transactions, ICategoryStore categories, IApprovingProposal approving,
    IClock clock, ILogger<CategoriseProposal> logger)
    : IProposalHandler
{
    public const string KindName = "banking.categorise_transactions";
    public const int Most = 100;

    public string Module => "banking";
    public string Kind => KindName;
    public int PayloadVersion => 1;
    public bool NeedsTarget => false;

    private sealed record Entry(Guid Transaction, Guid Category);

    /// <summary>A line of the payload as the actor sees it: <paramref name="Line"/> null when unknown or unreadable.</summary>
    private sealed record Seen(Entry Entry, BankTransaction? Line, BankAccount? Account, bool CanWrite);

    public async Task<Result<ProposalCheck>> ValidateAsync(ItemId target, JsonElement payload, bool edited, CancellationToken ct)
    {
        var parsed = Parse(payload);
        if (!parsed.TryGetValue(out var entries))
            return Result.Failure<ProposalCheck>(FailureOf(parsed));
        var named = (await categories.AllAsync(ct)).ToDictionary(c => c.Id);
        for (var i = 0; i < entries!.Count; i++)
        {
            if (!named.TryGetValue(entries[i].Category, out var category))
                return Result.Failure<ProposalCheck>(new NotFoundFailure("bank category", entries[i].Category.ToString()));
            // A merged category is archived too: its lines went to its target.
            if (category.ArchivedAt is not null)
                return Result.Failure<ProposalCheck>(FieldErrors.At(
                    new Failure(BankingCodes.CategoryArchived, "A line takes only a category in use.", null), $"payload.lines[{i}].categoryId"));
        }
        // Submitted, every line is one the submitter reads (an unknown one answers the same: its existence is not told). Approved, a line
        // the approver no longer reads is skipped, not refused.
        if (approving.Id is null && (await SeeAsync(entries, ct)).FirstOrDefault(s => s.Line is null) is { } unread)
            return BankLines.NotFound<ProposalCheck>(unread.Entry.Transaction);
        return Result.Success(new ProposalCheck(null, [], Normalized(entries)));
    }

    public Failure? RefuseEdit(JsonElement submitted, JsonElement edited)
        => new(BankingCodes.ProposalNotEditable, "Proposed categories are not edited: reject them and categorise the lines by hand.", null);

    /// <summary>
    /// `{ count, hidden, lines [{ transactionId, bookedOn, label, accountName, amount, currency, current, proposed, applies }] }`: only the lines
    /// the viewer reads, the others counted in `hidden`; `applies` is false for a line the viewer does not write or a person categorised.
    /// </summary>
    public async Task<JsonElement> PreviewAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        if (!Parse(payload).TryGetValue(out var entries))
            return TransactionLinker.Element([]);
        var named = (await categories.AllAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
        var seen = await SeeAsync(entries!, ct);
        var lines = new JsonArray();
        foreach (var s in seen.Where(s => s.Line is not null))
            lines.Add((JsonNode)new JsonObject
            {
                ["transactionId"] = s.Line!.Id.ToString("D"), ["bookedOn"] = TransactionLinker.Day(s.Line.BookedOn), ["label"] = s.Line.Label,
                ["accountName"] = s.Account!.Name, ["amount"] = TransactionLinker.Amount(s.Line.Amount), ["currency"] = s.Line.Currency.Code,
                ["current"] = s.Line.Category is { } current ? named.GetValueOrDefault(current) : null,
                ["proposed"] = named.GetValueOrDefault(s.Entry.Category),
                ["applies"] = Applies(s),
            });
        return TransactionLinker.Element(new JsonObject { ["count"] = entries!.Count, ["hidden"] = entries.Count - lines.Count, ["lines"] = lines });
    }

    /// <summary>The ids of the lines categorised; the skipped ones are the payload's others.</summary>
    public async Task<Result<IReadOnlyList<Guid>>> ApplyAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        var parsed = Parse(payload);
        if (!parsed.TryGetValue(out var entries))
            return Result.Failure<IReadOnlyList<Guid>>(FailureOf(parsed));
        var applying = (await SeeAsync(entries!, ct)).Where(Applies).Select(s => s.Entry).ToList();
        var by = $"person:{access.Person.Value:D}";
        var now = clock.GetCurrentInstant();
        var applied = 0;
        // Not byPerson: the store's precedence then leaves a hand or assistant category set since the read above.
        foreach (var group in applying.GroupBy(e => e.Category))
            applied += await categories.SetForLinesAsync([.. group.Select(e => e.Transaction)], group.Key, CategorySource.Assistant, by, now, byPerson: false, ct);
        logger.LogInformation("Categorise proposal: {Applied} lines applied, {Skipped} skipped", applied, entries!.Count - applied);
        return Result.Success<IReadOnlyList<Guid>>([.. applying.Select(e => e.Transaction)]);
    }

    private static bool Applies(Seen s) => s is { Line: not null, CanWrite: true }
        && s.Line.CategorySource is not (CategorySource.Hand or CategorySource.Assistant);

    private async Task<IReadOnlyList<Seen>> SeeAsync(IReadOnlyList<Entry> entries, CancellationToken ct)
    {
        var readable = (await access.ReadableAsync(await accounts.AllAsync(ct), ct)).ToDictionary(r => r.Account.Id);
        var seen = new List<Seen>();
        foreach (var entry in entries)
            seen.Add(await transactions.FindAsync(entry.Transaction, ct) is { } line && readable.TryGetValue(line.Account, out var account)
                ? new Seen(entry, line, account.Account, account.CanWrite)
                : new Seen(entry, null, null, false));
        return seen;
    }

    private static Result<IReadOnlyList<Entry>> Parse(JsonElement payload)
    {
        if (!payload.TryGetProperty("lines", out var list) || list.ValueKind != JsonValueKind.Array)
            return Result.Failure<IReadOnlyList<Entry>>(FieldErrors.Shape([new FieldError("payload.lines", FieldErrors.ShapeCode, "A list of lines is required.")]));
        var errors = new List<FieldError>();
        var entries = new List<Entry>();
        var i = 0;
        foreach (var item in list.EnumerateArray())
        {
            Guid transaction = default, category = default;
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("transactionId", out var t) || t.ValueKind != JsonValueKind.String || !t.TryGetGuid(out transaction))
                errors.Add(new FieldError($"payload.lines[{i}].transactionId", FieldErrors.ShapeCode, "A UUID is required."));
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("categoryId", out var c) || c.ValueKind != JsonValueKind.String || !c.TryGetGuid(out category))
                errors.Add(new FieldError($"payload.lines[{i}].categoryId", FieldErrors.ShapeCode, "A UUID is required."));
            entries.Add(new Entry(transaction, category));
            i++;
        }
        if (errors.Count > 0)
            return Result.Failure<IReadOnlyList<Entry>>(FieldErrors.Shape(errors));
        if (entries.Count is 0 or > Most || entries.DistinctBy(e => e.Transaction).Count() != entries.Count)
            return Result.Failure<IReadOnlyList<Entry>>(FieldErrors.At(
                new Failure(BankingCodes.ProposalLines, $"1 to {Most} lines, each named once.", null), "payload.lines"));
        return Result.Success<IReadOnlyList<Entry>>(entries);
    }

    private static JsonElement Normalized(IReadOnlyList<Entry> entries) => TransactionLinker.Element(new JsonObject
    {
        ["lines"] = new JsonArray([.. entries.Select(e => (JsonNode)new JsonObject
        {
            ["transactionId"] = e.Transaction.ToString("D"), ["categoryId"] = e.Category.ToString("D"),
        })]),
    });

    private static Failure FailureOf<T>(Result<T> result) where T : notnull
        => result.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException();
}

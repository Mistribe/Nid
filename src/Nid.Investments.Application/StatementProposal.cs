using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Investments.Application;

/// <summary>
/// Spec 4D1 §2: `{ asOf, value?, movements[{ on, kind, amount, label?, ref? }], note?, excluded? }` on an envelope; subject
/// key: the date. A movement Nid already holds (same reference, or without one the same date, kind and amount) is warned
/// and, unless an edit's `excluded` says otherwise, left out when applied. `excluded` is an approval's edit only (D5): a
/// submitter's is dropped. Nothing left to write and the value already recorded on `asOf` (or none given): « conforme ».
/// </summary>
public sealed class StatementProposal(
    IEnvelopeStore envelopes, IItemRegistry items, IHouseholdCalendar calendar, IValuations valuations, IFlows flows, IInvoker invoker)
    : IProposalHandler
{
    public const int MaxMovements = 200;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Kinds = ["contribution", "withdrawal", "income", "expense"]; // never `opening`

    public string Module => "investments";
    public string Kind => "investments.record_statement";
    public int PayloadVersion => 1;

    private sealed record Body(string? AsOf, AmountBody? Value, List<MovementBody?>? Movements, string? Note, List<int>? Excluded);

    private sealed record AmountBody(string? Amount, string? Currency);

    private sealed record MovementBody(string? On, string? Kind, string? Amount, string? Label, string? Ref);

    private sealed record Movement(LocalDate On, string Kind, decimal Amount, string? Label, string? Ref);

    private sealed record Parsed(LocalDate AsOf, Money? Value, IReadOnlyList<Movement> Movements, string? Note, IReadOnlyList<int>? Excluded);

    private enum Mark { None, Match, Reference }

    public async Task<Result<ProposalCheck>> ValidateAsync(ItemId target, JsonElement payload, bool edited, CancellationToken ct)
    {
        var parsed = Parse(payload);
        if (!parsed.TryGetValue(out var p))
            return Result.Failure<ProposalCheck>(FailureOf(parsed));
        if (!edited)
            p = p! with { Excluded = null };
        var loaded = await EnvelopeCheck.LoadAsync(envelopes, items, target, ct);
        if (!loaded.TryGetValue(out var envelope))
            return Result.Failure<ProposalCheck>(FailureOf(loaded));
        if (EnvelopeCheck.RequireOpen(envelope.Item).TryGetFailure(out var closed))
            return Result.Failure<ProposalCheck>(closed!);
        if (p!.Value is { } value && !string.Equals(envelope.Item.Currency, value.Currency.Code, StringComparison.Ordinal))
            return Field(CoreFactCodes.CurrencyMismatch, "The value is not in the envelope's currency.", "payload.value.currency");
        if (p.AsOf > await calendar.TodayAsync(ct))
            return Field(CoreFactCodes.DateInFuture, "A statement is dated today or before.", "payload.asOf");
        for (var i = 0; i < p.Movements.Count; i++)
            if (p.Movements[i].On > p.AsOf)
                return Field(FieldErrors.ShapeCode, "A movement is dated on or before the statement.", $"payload.movements[{i}].on");

        var marked = await RecordedAsync(target, p.Movements, p.Excluded, ct);
        if (!marked.TryGetValue(out var marks))
            return Result.Failure<ProposalCheck>(FailureOf(marked));
        if (p.Excluded is { } excluded)
            for (var i = 0; i < p.Movements.Count; i++)
                if (!excluded.Contains(i) && marks![i] == Mark.Reference)
                    return Field(CoreFactCodes.ExternalRefTaken, "This reference is already recorded: leave this movement out.", $"payload.movements[{i}].ref");

        var warnings = Enumerable.Range(0, p.Movements.Count).Where(i => marks![i] != Mark.None)
            .Select(i => new ProposalWarning(ProposalCodes.AlreadyRecorded, "info", $"Movement {i + 1} is already recorded.", $"payload.movements[{i}]"))
            .ToList();
        var conforme = Enumerable.Range(0, p.Movements.Count).Any(i => Writes(p, marks!, i)) ? null : await ConformeAsync(target, p, marks!, ct);
        return Result.Success(new ProposalCheck(Iso(p.AsOf), warnings, Normalized(p), conforme));
    }

    /// <summary>With no movement to write: `{ expected, given }` when the value is the one recorded on `asOf` (or none is given), else null.</summary>
    private async Task<JsonElement?> ConformeAsync(ItemId target, Parsed p, Mark[] marks, CancellationToken ct)
    {
        Valuation? recorded = null;
        if (p.Value is { } value)
        {
            recorded = (await valuations.LatestAsync(target, p.AsOf, ct)).TryGetValue(out var latest) && latest!.Value is { } v && v.AsOf == p.AsOf ? v : null;
            if (recorded?.Value.Amount != value.Amount)
                return null;
        }
        return JsonSerializer.SerializeToElement(new
        {
            expected = new { value = recorded is null ? null : AmountView.From(recorded.Value), recorded = marks.Count(m => m != Mark.None) },
            given = new { value = AmountView.From(p.Value), movements = p.Movements.Count },
        }, Json);
    }

    /// <summary>Whether applying writes movement <paramref name="i"/>: the edit's choice, else whatever Nid does not hold yet.</summary>
    private static bool Writes(Parsed p, Mark[] marks, int i) => p.Excluded is { } excluded ? !excluded.Contains(i) : marks[i] == Mark.None;

    public async Task<JsonElement> PreviewAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        if (!Parse(payload).TryGetValue(out var p))
            return JsonSerializer.SerializeToElement(new { value = (object?)null, movements = Array.Empty<object>() });
        var currency = (await items.FindAsync(target, ct))?.Currency ?? "";
        var marks = (await RecordedAsync(target, p!.Movements, p.Excluded, ct)).TryGetValue(out var m) ? m! : new Mark[p.Movements.Count];
        return JsonSerializer.SerializeToElement(new
        {
            value = p.Value is { } v ? await ValuationProposal.CompareAsync(valuations, target, p.AsOf, v, ct) : null,
            movements = p.Movements.Select((x, i) => new
            {
                index = i, on = Iso(x.On), kind = x.Kind, amount = new AmountView(Text(x.Amount), currency), label = x.Label, recorded = marks[i] != Mark.None,
            }),
        }, Json);
    }

    public async Task<Result<IReadOnlyList<Guid>>> ApplyAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        var parsed = Parse(payload);
        if (!parsed.TryGetValue(out var p))
            return Result.Failure<IReadOnlyList<Guid>>(FailureOf(parsed));
        var marked = await RecordedAsync(target, p!.Movements, p.Excluded, ct);
        if (!marked.TryGetValue(out var marks))
            return Result.Failure<IReadOnlyList<Guid>>(FailureOf(marked));
        var currency = (await items.FindAsync(target, ct))?.Currency;

        var applied = new List<Guid>();
        for (var i = 0; i < p.Movements.Count; i++)
        {
            if (!Writes(p, marks!, i))
                continue;
            var m = p.Movements[i];
            if (!Money.TryParse(Text(m.Amount), currency, out var amount))
                return Result.Failure<IReadOnlyList<Guid>>(FieldErrors.At(
                    new Failure(CoreFactCodes.CurrencyMismatch, "The envelope has no currency.", null), $"payload.movements[{i}].amount"));
            // The line's text is the flow's note; the bank's reference its external reference (deduplication).
            var flow = await invoker.InvokeAsync(new RecordFlowCommand(target, new FlowDraft(m.On, m.Kind, amount, null, m.Ref, m.Label)), ct);
            if (!flow.TryGetValue(out var flowId))
                return Result.Failure<IReadOnlyList<Guid>>(FieldErrors.At(FailureOf(flow), CoreField(FailureOf(flow), $"payload.movements[{i}]")));
            applied.Add(flowId);
        }
        if (p.Value is { } value)
        {
            var valued = await invoker.InvokeAsync(new RecordValuationCommand(target, p.AsOf, value, p.Note), ct);
            if (!valued.TryGetValue(out var valuationId))
                return Result.Failure<IReadOnlyList<Guid>>(FieldErrors.At(FailureOf(valued), "payload.value"));
            applied.Add(valuationId);
        }
        return Result.Success<IReadOnlyList<Guid>>(applied);
    }

    /// <summary>Each movement against the envelope's current flows, read once.</summary>
    private async Task<Result<Mark[]>> RecordedAsync(ItemId target, IReadOnlyList<Movement> movements, IReadOnlyList<int>? excluded, CancellationToken ct)
    {
        var listed = await flows.ListAsync(target, EnvelopeCheck.Everything, null, ct);
        if (!listed.TryGetValue(out var existing))
            return Result.Failure<Mark[]>(FailureOf(listed));
        var marks = new Mark[movements.Count];
        var references = movements.Select(m => m.Ref).OfType<string>().ToHashSet();
        for (var i = 0; i < movements.Count; i++)
            if (movements[i].Ref is { } reference)
                marks[i] = existing!.Any(f => f.ExternalRef == reference) ? Mark.Reference : Mark.None;
        // Without a reference, by count: the n-th identical line is recorded only if at least n identical flows exist. A flow
        // a line's reference matched is that line's, never also another's; an excluded line counts last, never taking an included one's.
        var available = existing!.Where(f => f.ExternalRef is not { } r || !references.Contains(r))
            .GroupBy(f => (f.OccurredOn, Kind: EnvelopeWire.FlowWire(f.Kind), f.Amount.Amount)).ToDictionary(g => g.Key, g => g.Count());
        foreach (var i in Enumerable.Range(0, movements.Count).Where(i => movements[i].Ref is null).OrderBy(i => excluded?.Contains(i) == true))
        {
            var key = (movements[i].On, movements[i].Kind, movements[i].Amount);
            if (available.TryGetValue(key, out var left) && left > 0)
            {
                available[key] = left - 1;
                marks[i] = Mark.Match;
            }
        }
        return Result.Success(marks);
    }

    /// <summary>The known fields only: what is fingerprinted and stored.</summary>
    private static JsonElement Normalized(Parsed p)
    {
        var body = new JsonObject { ["asOf"] = Iso(p.AsOf) };
        if (p.Value is { } value)
            body["value"] = JsonSerializer.SerializeToNode(AmountView.From(value), Json);
        body["movements"] = new JsonArray(p.Movements.Select(m =>
        {
            var line = new JsonObject { ["on"] = Iso(m.On), ["kind"] = m.Kind, ["amount"] = Text(m.Amount) };
            if (m.Label is not null) line["label"] = m.Label;
            if (m.Ref is not null) line["ref"] = m.Ref;
            return (JsonNode?)line;
        }).ToArray());
        if (p.Note is not null)
            body["note"] = p.Note;
        if (p.Excluded is { } excluded)
            body["excluded"] = new JsonArray(excluded.Select(i => (JsonNode?)i).ToArray());
        return JsonSerializer.SerializeToElement(body);
    }

    private static Result<Parsed> Parse(JsonElement payload)
    {
        Body? body;
        try
        {
            body = payload.Deserialize<Body>(Json);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) // InvalidOperationException: an undefined element
        {
            body = null;
        }
        var errors = new List<FieldError>();
        void Error(string path, string message) => errors.Add(new FieldError(path, FieldErrors.ShapeCode, message));

        LocalDate asOf = default;
        if (body?.AsOf is not { } date || LocalDatePattern.Iso.Parse(date) is not { Success: true } parsedDate)
            Error("payload.asOf", "An ISO date (yyyy-mm-dd) is required.");
        else
            asOf = parsedDate.Value;

        Money? value = null;
        if (body?.Value is { } v)
        {
            if (!Currency.TryCreate(v.Currency, out _))
                Error("payload.value.currency", "A currency (ISO code) is required.");
            else if (!Money.TryParse(v.Amount, v.Currency, out var money))
                Error("payload.value.amount", "A decimal amount with a point (e.g. '1234.56') is required.");
            else
                value = money;
        }

        var raw = body?.Movements ?? [];
        var movements = new List<Movement>();
        if (raw.Count > MaxMovements)
            Error("payload.movements", $"{MaxMovements} movements at most: split the statement by date, each part with its own asOf (an "
                + "earlier part's asOf is its last movement's date); never send the same asOf twice, as it replaces the earlier part.");
        else
            for (var i = 0; i < raw.Count; i++)
            {
                var m = raw[i];
                var at = $"payload.movements[{i}]";
                LocalDate on = default;
                if (m?.On is not { } day || LocalDatePattern.Iso.Parse(day) is not { Success: true } parsedDay)
                    Error($"{at}.on", "An ISO date (yyyy-mm-dd) is required.");
                else
                    on = parsedDay.Value;
                if (m?.Kind is not { } kind || !Kinds.Contains(kind))
                    Error($"{at}.kind", "One of contribution, withdrawal, income, expense.");
                if (!decimal.TryParse(m?.Amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
                    || amount <= 0 || decimal.Round(amount, Money.MaxDecimals) != amount)
                    Error($"{at}.amount", "A positive decimal amount with a point (e.g. '1234.56'), in the envelope's currency.");
                var label = Clean(m?.Label);
                var reference = Clean(m?.Ref);
                if (label is { Length: > 200 })
                    Error($"{at}.label", "200 characters at most.");
                if (reference is { Length: > 100 })
                    Error($"{at}.ref", "100 characters at most.");
                else if (reference is not null && reference.Any(char.IsControl))
                    Error($"{at}.ref", "No control character.");
                else if (reference is not null && movements.Any(x => x.Ref == reference))
                    Error($"{at}.ref", "Each reference appears once in a statement.");
                movements.Add(new Movement(on, m?.Kind ?? "", amount, label, reference));
            }

        if (value is null && body?.Value is null && raw.Count == 0)
            Error("payload.movements", "A value or at least one movement is required.");
        if (body?.Note is { Length: > 500 })
            Error("payload.note", "500 characters at most.");
        var excluded = body?.Excluded?.Distinct().Order().ToList();
        if (excluded is not null && excluded.Any(x => x < 0 || x >= raw.Count))
            Error("payload.excluded", "The indexes of the statement's movements.");
        return errors.Count > 0
            ? Result.Failure<Parsed>(FieldErrors.Shape(errors))
            : Result.Success(new Parsed(asOf, value, movements, body!.Note, excluded));
    }

    /// <summary>The Core's own refused field under the movement, by the payload's name (`external_ref` is `ref`, `note` is `label`), else the movement.</summary>
    public static string CoreField(Failure failure, string at)
        => failure.Metadata is { } m && m.TryGetValue("field", out var field) && field is string name
            ? $"{at}.{name switch { "external_ref" => "ref", "note" => "label", _ => name }}"
            : at;

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static Failure FailureOf<T>(Result<T> result) where T : notnull
        => result.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException();

    private static Result<ProposalCheck> Field(string code, string message, string path)
        => Result.Failure<ProposalCheck>(FieldErrors.At(new Failure(code, message, null), path));

    private static string Iso(LocalDate date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Text(decimal amount) => amount.ToString(CultureInfo.InvariantCulture);
}

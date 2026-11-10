using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Investments.Application;

/// <summary>
/// Issue #34 §6: `{ asOf, sourceLabel?, lines[{ name, isin?, kind?, units?, unitPrice?, value, costBasisPerUnit? }],
/// movements[{ holding, on, kind, units?, unitPrice?, amount, label?, ref? }], recordValuation? }` on an envelope; subject key: the date.
/// A line is the holding of its ISIN, else of its exact name, else a new holding (its kind then required). A movement's `holding` is a
/// line's or a holding's ISIN or name. A movement Nid already holds (same reference, or without one the same holding, date, kind and
/// amount) is warned and left out. Checked by the rules the API uses (<see cref="HoldingRules"/>, <see cref="MovementRules"/>),
/// applied through its commands, in the approval's transaction.
/// </summary>
public sealed partial class HoldingsSnapshotProposal(
    IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings, IFlows flows, IValuations valuations, IHouseholdCalendar calendar,
    IInvoker invoker)
    : IProposalHandler
{
    public const int MaxLines = 100;
    public const int MaxMovements = 200;

    /// <summary>A line whose value moves more than this share of the previous one, net of money in and out, is flagged.</summary>
    public const decimal LargeChangeShare = 0.30m;

    public const string LargeChange = "large_change";

    public string Module => "investments";
    public string Kind => "investments.record_holdings_snapshot";
    public int PayloadVersion => 1;

    private sealed record Body(string? AsOf, string? SourceLabel, List<LineBody?>? Lines, List<MovementBody?>? Movements, bool? RecordValuation);

    private sealed record LineBody(string? Name, string? Isin, string? Kind, string? Units, string? UnitPrice, string? Value, string? CostBasisPerUnit);

    private sealed record MovementBody(string? Holding, string? On, string? Kind, string? Units, string? UnitPrice, string? Amount, string? Label, string? Ref);

    private sealed record Line(string Name, string? Isin, string? Kind, decimal? Units, decimal? UnitPrice, decimal Value, decimal? CostBasisPerUnit);

    private sealed record Movement(string Holding, LocalDate On, string Kind, decimal? Units, decimal? UnitPrice, decimal Amount, string? Label, string? Ref);

    private sealed record Parsed(LocalDate AsOf, string? SourceLabel, IReadOnlyList<Line> Lines, IReadOnlyList<Movement> Movements, bool RecordValuation);

    /// <summary>A line's holding: an existing one, or a new one under a temporary id (shared by lines naming the same).</summary>
    private sealed record LineHolding(Guid Id, bool IsNew, string Kind);

    /// <summary>The payload against the envelope: each line's and movement's holding, what is already recorded, the large changes.</summary>
    private sealed record Plan(
        Parsed P, MovementFacts Facts, LineHolding[] Lines, Guid?[] MovementHolding, bool[] Recorded,
        (LocalDate AsOf, decimal Value)?[] Previous, bool[] Large);

    private sealed record PreviousValue(string AsOf, AmountView Value);

    private sealed record LinePreview(
        int Index, string Name, string? Isin, string Kind, bool IsNew, string? Units, string? UnitPrice, AmountView Value,
        string? CostBasisPerUnit, PreviousValue? Previous, bool LargeChange);

    private sealed record MovementPreview(
        int Index, string Holding, string On, string Kind, string? Units, string? UnitPrice, AmountView Amount, string? Label, bool Recorded);

    private sealed record Preview(
        string Currency, AmountView Total, bool RecordValuation, ValuationComparison? Valuation,
        IReadOnlyList<LinePreview> Lines, IReadOnlyList<MovementPreview> Movements);

    /// <summary>What a payload that cannot be read or planned previews: no `recordValuation` nor `valuation`.</summary>
    private sealed record EmptyPreview(string? Currency, AmountView? Total, IReadOnlyList<LinePreview> Lines, IReadOnlyList<MovementPreview> Movements);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Body))]
    [JsonSerializable(typeof(Preview))]
    [JsonSerializable(typeof(EmptyPreview))]
    [JsonSerializable(typeof(JsonObject))]
    private sealed partial class HoldingsSnapshotJson : JsonSerializerContext;

    public async Task<Result<ProposalCheck>> ValidateAsync(ItemId target, JsonElement payload, bool edited, CancellationToken ct)
    {
        var parsed = Parse(payload);
        if (!parsed.TryGetValue(out var p))
            return Result.Failure<ProposalCheck>(FailureOf(parsed));
        var planned = await PlanAsync(target, p!, write: true, ct);
        if (!planned.TryGetValue(out var plan))
            return Result.Failure<ProposalCheck>(FailureOf(planned));
        var facts = plan!.Facts;
        var today = await calendar.TodayAsync(ct);

        // The new holdings, then the snapshot and the movements against them: the API's own rules.
        var known = facts.Holdings.ToList();
        for (var i = 0; i < p!.Lines.Count; i++)
        {
            var line = plan.Lines[i];
            if (!line.IsNew || known.Any(h => h.Holding.Id == line.Id))
                continue;
            if (p.Lines[i].Kind is null)
                return Field(InvestmentsCodes.UnknownHoldingKind, "A holding Nid does not have yet needs its kind.", $"payload.lines[{i}].kind");
            var holding = HoldingRules.Check(line.Id, target, Draft(p.Lines[i]), known);
            if (!holding.TryGetValue(out var h))
                return Result.Failure<ProposalCheck>(At(FailureOf(holding), $"payload.lines[{i}]"));
            known.Add(new StoredHolding(h!, 0));
        }
        var snapshot = HoldingRules.Check(Guid.Empty, target, Snapshot(p, plan.Lines.Select(l => l.Id).ToList(), facts.Currency), known, facts.Currency, today);
        if (!snapshot.TryGetValue(out var checkedSnapshot))
            return Result.Failure<ProposalCheck>(At(FailureOf(snapshot), "payload", line: true));
        for (var i = 0; i < p.Movements.Count; i++)
        {
            var m = p.Movements[i];
            var at = $"payload.movements[{i}]";
            if (plan.MovementHolding[i] is not { } holdingId)
                return Field(InvestmentsCodes.InvalidMovement, "No line nor holding of this envelope has this ISIN or name.", $"{at}.holding");
            if (m.On > p.AsOf)
                return Field(InvestmentsCodes.InvalidMovement, "A movement is dated on or before the statement.", $"{at}.on");
            var movement = MovementRules.Check(target, holdingId, m.Kind, m.On, m.Units, m.UnitPrice, Amounts.Of(m.Amount, facts.Currency), null, null,
                m.Label, m.Ref, known, facts.Flows, facts.Currency, today);
            if (movement.TryGetFailure(out var refused))
                return Result.Failure<ProposalCheck>(At(refused!, at));
        }

        var lineOf = plan.Lines.Select((l, i) => (l.Id, i)).DistinctBy(x => x.Id).ToDictionary(x => x.Id, x => x.i);
        var warnings = new List<ProposalWarning>();
        warnings.AddRange(Enumerable.Range(0, p.Lines.Count).Where(i => plan.Large[i]).Select(i => new ProposalWarning(LargeChange, "warning",
            $"Line {i + 1} moves more than {LargeChangeShare * 100:0} % since the previous snapshot, net of money in and out.", $"payload.lines[{i}].value")));
        warnings.AddRange(HoldingMath.Warnings(checkedSnapshot!, await HoldingCheck.ValuationOnAsync(valuations, target, p.AsOf, ct)).Select(w =>
            w.HoldingId is { } id
                ? new ProposalWarning(w.Code, "warning", $"Line {lineOf[id] + 1}: units × unit price is not its value.", $"payload.lines[{lineOf[id]}].value")
                : new ProposalWarning(w.Code, "warning", "The lines do not sum to the envelope's value of that date.", "payload.lines")));
        warnings.AddRange(Enumerable.Range(0, p.Movements.Count).Where(i => plan.Recorded[i])
            .Select(i => new ProposalWarning(ProposalCodes.AlreadyRecorded, "info", $"Movement {i + 1} is already recorded.", $"payload.movements[{i}]")));
        return Result.Success(new ProposalCheck(Iso(p.AsOf), warnings, Normalized(p)));
    }

    public async Task<JsonElement> PreviewAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        if (!Parse(payload).TryGetValue(out var p) || !(await PlanAsync(target, p!, write: false, ct)).TryGetValue(out var plan))
            return JsonSerializer.SerializeToElement(new EmptyPreview(null, null, [], []), HoldingsSnapshotJson.Default.EmptyPreview);
        var currency = plan!.Facts.Currency;
        var names = plan.Facts.Holdings.ToDictionary(h => h.Holding.Id, h => h.Holding.Name);
        var total = Amounts.Of(p!.Lines.Sum(l => l.Value), currency);
        return JsonSerializer.SerializeToElement(new Preview(
            currency.Code,
            AmountView.From(total),
            p.RecordValuation,
            p.RecordValuation ? await ValuationProposal.CompareAsync(valuations, target, p.AsOf, total, ct) : null,
            [.. p.Lines.Select((l, i) => new LinePreview(
                i, l.Name, l.Isin, plan.Lines[i].Kind, plan.Lines[i].IsNew,
                Text(l.Units), Text(l.UnitPrice), AmountView.From(Amounts.Of(l.Value, currency)),
                Text(l.CostBasisPerUnit),
                plan.Previous[i] is { } prev ? new PreviousValue(Iso(prev.AsOf), AmountView.From(Amounts.Of(prev.Value, currency))) : null,
                plan.Large[i]))],
            [.. p.Movements.Select((m, i) => new MovementPreview(
                i, plan.MovementHolding[i] is { } id && names.TryGetValue(id, out var name) ? name : m.Holding,
                Iso(m.On), m.Kind, Text(m.Units), Text(m.UnitPrice),
                AmountView.From(Amounts.Of(m.Amount, currency)), m.Label, plan.Recorded[i]))]),
            HoldingsSnapshotJson.Default.Preview);
    }

    public async Task<Result<IReadOnlyList<Guid>>> ApplyAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        var parsed = Parse(payload);
        if (!parsed.TryGetValue(out var p))
            return Result.Failure<IReadOnlyList<Guid>>(FailureOf(parsed));
        var planned = await PlanAsync(target, p!, write: true, ct);
        if (!planned.TryGetValue(out var plan))
            return Result.Failure<IReadOnlyList<Guid>>(FailureOf(planned));
        var currency = plan!.Facts.Currency;

        // The new holdings, then the movements, each as one write: a statement of a hundred lines is not a hundred round trips.
        var applied = new List<Guid>();
        var created = new Dictionary<Guid, Guid>(); // temporary id → the new holding's
        var newLines = new List<int>();
        for (var i = 0; i < p!.Lines.Count; i++)
            if (plan.Lines[i] is { IsNew: true } line && newLines.All(n => plan.Lines[n].Id != line.Id))
                newLines.Add(i);
        if (newLines.Count > 0)
        {
            var added = await invoker.InvokeAsync(new AddHoldingsCommand(target, newLines.Select(i => Draft(p.Lines[i])).ToList()), ct);
            if (!added.TryGetValue(out var ids))
            {
                var failure = FailureOf(added);
                return Result.Failure<IReadOnlyList<Guid>>(At(failure, $"payload.lines[{newLines[Batch.IndexOf(failure) ?? 0]}]"));
            }
            for (var k = 0; k < newLines.Count; k++)
                created[plan.Lines[newLines[k]].Id] = ids![k];
            applied.AddRange(ids!);
        }
        Guid Real(Guid id) => created.GetValueOrDefault(id, id);

        var recorded = await invoker.InvokeAsync(new RecordHoldingsSnapshotCommand(target, null,
            Snapshot(p, plan.Lines.Select(l => Real(l.Id)).ToList(), currency)), ct);
        if (!recorded.TryGetValue(out var snapshot))
            return Result.Failure<IReadOnlyList<Guid>>(At(FailureOf(recorded), "payload", line: true));
        applied.Add(snapshot!.Id);
        if (snapshot.ValuationId is { } valuation)
            applied.Add(valuation); // so an approval stamps its source on the value (tranche 3 §4.1)

        var moved = new List<int>();
        var lines = new List<HoldingMovementLine>();
        for (var i = 0; i < p.Movements.Count; i++)
        {
            if (plan.Recorded[i])
                continue;
            var m = p.Movements[i];
            if (plan.MovementHolding[i] is not { } holding)
                return Result.Failure<IReadOnlyList<Guid>>(FieldErrors.At(
                    new Failure(InvestmentsCodes.InvalidMovement, "No line nor holding of this envelope has this ISIN or name.", null), $"payload.movements[{i}].holding"));
            moved.Add(i);
            lines.Add(new HoldingMovementLine(Real(holding),
                new MovementDraft(m.On, m.Kind, m.Units, m.UnitPrice, Amounts.Of(m.Amount, currency), null, m.Label, m.Ref)));
        }
        if (lines.Count > 0)
        {
            var movements = await invoker.InvokeAsync(new AddHoldingMovementsCommand(target, lines), ct);
            if (!movements.TryGetValue(out var written))
            {
                var failure = FailureOf(movements);
                return Result.Failure<IReadOnlyList<Guid>>(At(failure, $"payload.movements[{moved[Batch.IndexOf(failure) ?? 0]}]"));
            }
            applied.AddRange(written!);
        }
        return Result.Success<IReadOnlyList<Guid>>(applied);
    }

    private async Task<Result<Plan>> PlanAsync(ItemId target, Parsed p, bool write, CancellationToken ct)
    {
        var loaded = await MovementFacts.LoadAsync(envelopes, items, holdings, flows, target, write, null, ct);
        if (!loaded.TryGetValue(out var facts))
            return Result.Failure<Plan>(FailureOf(loaded));
        var existing = facts!.Holdings.Select(h => h.Holding).ToList();

        // By ISIN, then by exact name: an existing holding, else one a line before proposed.
        var proposed = new List<(Guid Id, string Name, string? Isin, string Kind)>();
        var lines = new LineHolding[p.Lines.Count];
        for (var i = 0; i < p.Lines.Count; i++)
        {
            var l = p.Lines[i];
            var match = existing.FirstOrDefault(h => l.Isin is not null && h.Isin == l.Isin) ?? existing.FirstOrDefault(h => h.Name == l.Name);
            if (match is not null)
            {
                lines[i] = new LineHolding(match.Id, false, match.Kind);
                continue;
            }
            var earlier = proposed.FirstOrDefault(h => l.Isin is not null && h.Isin == l.Isin);
            if (earlier.Name is null)
                earlier = proposed.FirstOrDefault(h => h.Name == l.Name);
            if (earlier.Name is null)
            {
                earlier = (Guid.CreateVersion7(), l.Name, l.Isin, l.Kind ?? "");
                proposed.Add(earlier);
            }
            lines[i] = new LineHolding(earlier.Id, true, earlier.Kind);
        }

        var candidates = existing.Select(h => (h.Id, h.Name, h.Isin)).Concat(proposed.Select(h => (h.Id, h.Name, h.Isin))).ToList();
        var movementHolding = p.Movements.Select(m =>
        {
            var isin = m.Holding.ToUpperInvariant();
            var found = candidates.FirstOrDefault(c => c.Isin == isin);
            if (found.Name is null)
                found = candidates.FirstOrDefault(c => c.Name == m.Holding);
            return found.Name is null ? (Guid?)null : found.Id;
        }).ToArray();

        var recorded = Recorded(p.Movements, movementHolding, facts.Movements);

        // Each line against the holding's previous value: Δ value − (money in − out since), the proposal's movements included.
        var before = (await holdings.SnapshotsAsync(target, ct)).Where(s => s.AsOf < p.AsOf).ToList();
        var previous = new (LocalDate AsOf, decimal Value)?[p.Lines.Count];
        var large = new bool[p.Lines.Count];
        for (var i = 0; i < p.Lines.Count; i++)
        {
            if (lines[i].IsNew || HoldingMath.ValueAt(lines[i].Id, before, p.AsOf) is not { } prev)
                continue;
            previous[i] = prev;
            var id = lines[i].Id;
            var since = facts.Movements.Where(m => m.HoldingId == id && m.OccurredOn > prev.AsOf && m.OccurredOn <= p.AsOf).Select(m => (m.Kind, m.Amount))
                .Concat(p.Movements.Where((m, k) => movementHolding[k] == id && !recorded[k] && m.On > prev.AsOf).Select(m => (m.Kind, m.Amount)));
            var net = since.Sum(m => MovementKinds.IsMoneyIn(m.Kind) ? m.Amount : MovementKinds.IsMoneyOut(m.Kind) ? -m.Amount : 0m);
            large[i] = prev.Value > 0 && Math.Abs(p.Lines[i].Value - prev.Value - net) > LargeChangeShare * prev.Value;
        }
        return Result.Success(new Plan(p, facts, lines, movementHolding, recorded, previous, large));
    }

    /// <summary>As <see cref="StatementProposal"/>: by reference, else by count — the n-th identical line is recorded only if n identical movements exist.</summary>
    private static bool[] Recorded(IReadOnlyList<Movement> movements, Guid?[] holding, IReadOnlyList<HoldingMovement> existing)
    {
        var marks = new bool[movements.Count];
        var references = movements.Select(m => m.Ref).OfType<string>().ToHashSet();
        var available = existing.Where(m => m.ExternalRef is not { } r || !references.Contains(r))
            .GroupBy(m => (m.HoldingId, m.OccurredOn, m.Kind, m.Amount)).ToDictionary(g => g.Key, g => g.Count());
        for (var i = 0; i < movements.Count; i++)
        {
            var m = movements[i];
            if (m.Ref is { } reference)
                marks[i] = existing.Any(x => x.ExternalRef == reference);
            else if (holding[i] is { } h && available.TryGetValue((h, m.On, m.Kind, m.Amount), out var left) && left > 0)
            {
                available[(h, m.On, m.Kind, m.Amount)] = left - 1;
                marks[i] = true;
            }
        }
        return marks;
    }

    private static HoldingDraft Draft(Line l) => new(l.Name, l.Isin, l.Kind, null, null, null, null, null, null, null, null);

    private static SnapshotDraft Snapshot(Parsed p, IReadOnlyList<Guid> ids, Currency currency)
        => new(p.AsOf, p.SourceLabel, true,
            p.Lines.Select((l, i) => new SnapshotLineDraft(ids[i], l.Units, l.UnitPrice, Amounts.Of(l.Value, currency), l.CostBasisPerUnit)).ToList(),
            p.RecordValuation);

    /// <summary>A rule's refused field under <paramref name="prefix"/>, by the payload's name; a line's holding is its name.</summary>
    private static Failure At(Failure failure, string prefix, bool line = false)
    {
        if (failure.Metadata is not { } m || !m.TryGetValue("field", out var value) || value is not string field)
            return FieldErrors.At(failure, prefix);
        field = line ? field.Replace(".holdingId", ".name", StringComparison.Ordinal)
            : field switch { "holdingId" => "holding", "occurredOn" => "on", "externalRef" => "ref", _ => field };
        return FieldErrors.At(failure, $"{prefix}.{field}");
    }

    /// <summary>The known fields only: what is fingerprinted and stored.</summary>
    private static JsonElement Normalized(Parsed p)
    {
        static void Put(JsonObject o, string key, string? value)
        {
            if (value is not null) o[key] = value;
        }
        var body = new JsonObject { ["asOf"] = Iso(p.AsOf) };
        Put(body, "sourceLabel", p.SourceLabel);
        body["lines"] = new JsonArray(p.Lines.Select(l =>
        {
            var line = new JsonObject { ["name"] = l.Name };
            Put(line, "isin", l.Isin);
            Put(line, "kind", l.Kind);
            Put(line, "units", Text(l.Units));
            Put(line, "unitPrice", Text(l.UnitPrice));
            line["value"] = Text(l.Value);
            Put(line, "costBasisPerUnit", Text(l.CostBasisPerUnit));
            return (JsonNode?)line;
        }).ToArray());
        body["movements"] = new JsonArray(p.Movements.Select(m =>
        {
            var line = new JsonObject { ["holding"] = m.Holding, ["on"] = Iso(m.On), ["kind"] = m.Kind };
            Put(line, "units", Text(m.Units));
            Put(line, "unitPrice", Text(m.UnitPrice));
            line["amount"] = Text(m.Amount);
            Put(line, "label", m.Label);
            Put(line, "ref", m.Ref);
            return (JsonNode?)line;
        }).ToArray());
        if (p.RecordValuation)
            body["recordValuation"] = true;
        return JsonSerializer.SerializeToElement(body, HoldingsSnapshotJson.Default.JsonObject);
    }

    private static Result<Parsed> Parse(JsonElement payload)
    {
        Body? body;
        try
        {
            body = payload.Deserialize(HoldingsSnapshotJson.Default.Body);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) // InvalidOperationException: an undefined element
        {
            body = null;
        }
        var errors = new List<FieldError>();
        void Error(string path, string message) => errors.Add(new FieldError(path, FieldErrors.ShapeCode, message));

        LocalDate? Date(string? text, string path)
        {
            if (text is not null && LocalDatePattern.Iso.Parse(text) is { Success: true } parsed)
                return parsed.Value;
            Error(path, "An ISO date (yyyy-mm-dd) is required.");
            return null;
        }

        // Units and prices: 6 decimals at most (the rules say so), amounts 4; a units' sign is the kind's, never the document's.
        decimal? Number(string? text, string path, bool required = false)
        {
            if (text is null && !required)
                return null;
            if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var x))
                return x;
            Error(path, "A decimal with a point (e.g. '14.565') is required.");
            return null;
        }

        decimal Amount(string? text, string path, bool positive)
        {
            if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var x)
                && decimal.Round(x, Money.MaxDecimals) == x && (!positive || x > 0))
                return x;
            Error(path, $"A{(positive ? " positive" : "")} decimal amount with a point (e.g. '1234.56'), in the envelope's currency.");
            return 0;
        }

        var asOf = Date(body?.AsOf, "payload.asOf");

        var rawLines = body?.Lines ?? [];
        var lines = new List<Line>();
        if (rawLines.Count > MaxLines)
            Error("payload.lines", $"{MaxLines} lines at most.");
        else
            for (var i = 0; i < rawLines.Count; i++)
            {
                var l = rawLines[i];
                var at = $"payload.lines[{i}]";
                var name = Clean(l?.Name);
                if (name is null)
                    Error($"{at}.name", "The holding's name as printed is required.");
                lines.Add(new Line(name ?? "", Clean(l?.Isin)?.ToUpperInvariant(), Clean(l?.Kind), Number(l?.Units, $"{at}.units"),
                    Number(l?.UnitPrice, $"{at}.unitPrice"), Amount(l?.Value, $"{at}.value", positive: false), Number(l?.CostBasisPerUnit, $"{at}.costBasisPerUnit")));
            }

        var rawMovements = body?.Movements ?? [];
        var movements = new List<Movement>();
        if (rawMovements.Count > MaxMovements)
            Error("payload.movements", $"{MaxMovements} movements at most.");
        else
            for (var i = 0; i < rawMovements.Count; i++)
            {
                var m = rawMovements[i];
                var at = $"payload.movements[{i}]";
                var holding = Clean(m?.Holding);
                if (holding is null)
                    Error($"{at}.holding", "The holding's ISIN or name, as a line or Nid names it, is required.");
                var on = Date(m?.On, $"{at}.on");
                var kind = m?.Kind;
                if (kind is MovementKinds.SwitchIn or MovementKinds.SwitchOut)
                    Error($"{at}.kind", "A switch is not proposed from a statement: leave it out, the household records it in Nid.");
                else if (kind is null || !MovementKinds.All.Contains(kind))
                    Error($"{at}.kind", "One of allocation_in, allocation_out, maturity, fee, interest, income.");
                var units = Number(m?.Units, $"{at}.units");
                var reference = Clean(m?.Ref);
                if (reference is not null && reference.Any(char.IsControl))
                    Error($"{at}.ref", "No control character.");
                else if (reference is not null && movements.Any(x => x.Ref == reference))
                    Error($"{at}.ref", "Each reference appears once in a statement.");
                movements.Add(new Movement(holding ?? "", on ?? default, kind ?? "",
                    units is { } u && kind is not null && MovementKinds.All.Contains(kind) ? MovementKinds.IsIn(kind) ? Math.Abs(u) : -Math.Abs(u) : units,
                    Number(m?.UnitPrice, $"{at}.unitPrice"), Amount(m?.Amount, $"{at}.amount", positive: true), Clean(m?.Label), reference));
            }

        return errors.Count > 0
            ? Result.Failure<Parsed>(FieldErrors.Shape(errors))
            : Result.Success(new Parsed(asOf!.Value, Clean(body!.SourceLabel), lines, movements, body.RecordValuation == true));
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static Failure FailureOf<T>(Result<T> result) where T : notnull
        => result.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException();

    private static Result<ProposalCheck> Field(string code, string message, string path)
        => Result.Failure<ProposalCheck>(FieldErrors.At(new Failure(code, message, null), path));

    private static string Iso(LocalDate date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Text(decimal? x) => x?.ToString(CultureInfo.InvariantCulture);
}

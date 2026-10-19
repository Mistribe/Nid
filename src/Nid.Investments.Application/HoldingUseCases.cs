using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Investments.Application;

/// <summary>A holding as a body sends it (holdings spec §2.1); checked by <see cref="HoldingRules"/>.</summary>
public sealed record HoldingDraft(
    string? Name, string? Isin, string? Kind, string? AssetClass, string? Issuer, int? RiskIndicator,
    LocalDate? OpenedOn, LocalDate? MaturesOn, decimal? EntryCostPct, LocalDate? ClosedOn, string? Note);

public sealed record SnapshotLineDraft(Guid HoldingId, decimal? Units, decimal? UnitPrice, Money Value, decimal? CostBasisPerUnit);

public sealed record SnapshotDraft(LocalDate AsOf, string? SourceLabel, bool Complete, IReadOnlyList<SnapshotLineDraft> Lines, bool RecordValuation);

public sealed record AddHoldingCommand(ItemId ItemId, HoldingDraft Holding) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>A replacement in place; <paramref name="Version"/> is the holding's as the client read it.</summary>
public sealed record EditHoldingCommand(ItemId ItemId, Guid Holding, HoldingDraft Values, uint Version) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record DeleteHoldingCommand(ItemId ItemId, Guid Holding, uint Version) : ICommand<bool>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>Records a snapshot: <paramref name="SnapshotId"/> replaces that one (PUT), null replaces the one of its date or creates it (POST).</summary>
public sealed record RecordHoldingsSnapshotCommand(ItemId ItemId, Guid? SnapshotId, SnapshotDraft Snapshot) : ICommand<SnapshotRecorded>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record DeleteHoldingsSnapshotCommand(ItemId ItemId, Guid Snapshot) : ICommand<bool>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record GetHoldingsQuery(ItemId ItemId, LocalDate? On) : IQuery<HoldingsView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

public sealed record ListHoldingsSnapshotsQuery(ItemId ItemId) : IQuery<HoldingSnapshotList>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

/// <summary>Holdings spec §2, checked before anything is written.</summary>
internal static class HoldingRules
{
    public const int NameMax = 100;
    public const int SourceLabelMax = 200;

    /// <summary>The holding once checked: name and ISIN trimmed, the ISIN in capitals.</summary>
    public static Result<EnvelopeHolding> Check(Guid id, ItemId envelope, HoldingDraft d, IReadOnlyList<StoredHolding> others)
    {
        var isin = string.IsNullOrWhiteSpace(d.Isin) ? null : d.Isin.Trim().ToUpperInvariant();
        if (d.Name is null || d.Name.Trim().Length is < 1 or > NameMax)
            return Invalid<EnvelopeHolding>(InvestmentsCodes.InvalidHolding, "name", $"A name is 1 to {NameMax} characters, and not only whitespace.");
        if (d.Kind is null || !HoldingWire.Kinds.Contains(d.Kind))
            return Invalid<EnvelopeHolding>(InvestmentsCodes.UnknownHoldingKind, "kind", $"'{d.Kind}' is not a holding kind.");
        if (d.AssetClass is not null && !HoldingWire.AssetClasses.Contains(d.AssetClass))
            return Invalid<EnvelopeHolding>(InvestmentsCodes.InvalidHolding, "assetClass", $"'{d.AssetClass}' is not an asset class.");
        if (isin is not null && !HoldingMath.IsIsin(isin))
            return Invalid<EnvelopeHolding>(InvestmentsCodes.InvalidIsin, "isin", "An ISIN is two letters, nine letters or digits and a check digit.");
        if (isin is not null && others.Any(o => o.Holding.Id != id && o.Holding.Isin == isin))
            return Invalid<EnvelopeHolding>(InvestmentsCodes.InvalidHolding, "isin", "Another holding of this envelope has this ISIN.");
        if (d.Issuer is { Length: > EnvelopeRules.InstitutionMax })
            return Invalid<EnvelopeHolding>(InvestmentsCodes.InvalidHolding, "issuer", $"An issuer has at most {EnvelopeRules.InstitutionMax} characters.");
        if (d.RiskIndicator is < 1 or > 7)
            return Invalid<EnvelopeHolding>(InvestmentsCodes.InvalidHolding, "riskIndicator", "A risk indicator is 1 to 7.");
        if (d.EntryCostPct is { } cost && (cost is < 0 or > 100 || Math.Round(cost, 4) != cost))
            return Invalid<EnvelopeHolding>(InvestmentsCodes.InvalidHolding, "entryCostPct", "An entry cost is 0 to 100 percent, with 4 decimals at most.");
        if (d is { OpenedOn: { } opened, ClosedOn: { } closed } && closed < opened)
            return Invalid<EnvelopeHolding>(InvestmentsCodes.InvalidHolding, "closedOn", "A holding closes on or after it opens.");
        if (d.Note is { Length: > EnvelopeRules.NoteMax })
            return Invalid<EnvelopeHolding>(InvestmentsCodes.InvalidHolding, "note", $"A note has at most {EnvelopeRules.NoteMax} characters.");
        return Result.Success(new EnvelopeHolding(id, envelope, d.Name.Trim(), isin, d.Kind, d.AssetClass, d.Issuer, d.RiskIndicator,
            d.OpenedOn, d.MaturesOn, d.EntryCostPct, d.ClosedOn, d.Note));
    }

    /// <summary>The snapshot once checked against the envelope's holdings, its currency and today.</summary>
    public static Result<HoldingSnapshot> Check(Guid id, ItemId envelope, SnapshotDraft d, IReadOnlyList<StoredHolding> holdings, Currency currency, LocalDate today)
    {
        if (d.AsOf > today)
            return Invalid<HoldingSnapshot>(InvestmentsCodes.InvalidSnapshot, "asOf", "A snapshot is dated today or earlier.");
        if (d.SourceLabel is { Length: > SourceLabelMax })
            return Invalid<HoldingSnapshot>(InvestmentsCodes.InvalidSnapshot, "sourceLabel", $"A source label has at most {SourceLabelMax} characters.");
        if (d.Lines.Count == 0)
            return Invalid<HoldingSnapshot>(InvestmentsCodes.InvalidSnapshot, "lines", "A snapshot has at least one line.");
        var byId = holdings.ToDictionary(h => h.Holding.Id, h => h.Holding);
        for (var i = 0; i < d.Lines.Count; i++)
        {
            var line = d.Lines[i];
            var at = $"lines[{i}]";
            if (!byId.TryGetValue(line.HoldingId, out var holding))
                return Invalid<HoldingSnapshot>(InvestmentsCodes.InvalidSnapshot, $"{at}.holdingId", "This holding is not one of this envelope's.");
            if (d.Lines.Take(i).Any(l => l.HoldingId == line.HoldingId))
                return Invalid<HoldingSnapshot>(InvestmentsCodes.InvalidSnapshot, $"{at}.holdingId", "A holding appears once in a snapshot.");
            if (holding.ClosedOn is { } closed && closed < d.AsOf)
                return Invalid<HoldingSnapshot>(InvestmentsCodes.HoldingClosed, $"{at}.holdingId", "This holding was closed before the snapshot's date.");
            if (line.Value.Currency != currency)
                return Invalid<HoldingSnapshot>(InvestmentsCodes.InvalidSnapshot, $"{at}.value", "A line is in the envelope's currency.");
            if (line.Value.Amount < 0)
                return Invalid<HoldingSnapshot>(InvestmentsCodes.InvalidSnapshot, $"{at}.value", "A value is zero or more.");
            if (!HoldingWire.HasUnits(holding.Kind) && (line.Units is not null || line.UnitPrice is not null))
                return Invalid<HoldingSnapshot>(InvestmentsCodes.InvalidSnapshot, $"{at}.units", "A euro fund or cash line has no units and no unit price.");
            foreach (var (value, field) in new[] { (line.Units, "units"), (line.UnitPrice, "unitPrice"), (line.CostBasisPerUnit, "costBasisPerUnit") })
                if (value is { } x && (x < 0 || Math.Round(x, 6) != x))
                    return Invalid<HoldingSnapshot>(InvestmentsCodes.InvalidSnapshot, $"{at}.{field}", "Zero or more, with 6 decimals at most.");
        }
        return Result.Success(new HoldingSnapshot(id, envelope, d.AsOf, d.SourceLabel, d.Complete,
            d.Lines.Select(l => new SnapshotLine(l.HoldingId, l.Units, l.UnitPrice, l.Value.Amount, l.CostBasisPerUnit)).ToList()));
    }

    public static Failure Conflict() => new(InvestmentsCodes.HoldingConflict, "This holding was changed since you read it: reload it.", null);

    private static Result<T> Invalid<T>(string code, string field, string message) where T : notnull
        => Result.Failure<T>(new Failure(code, message, new Dictionary<string, object?> { ["field"] = field }));
}

/// <summary>The open envelope and its currency, for every holdings write.</summary>
internal static class HoldingCheck
{
    public static async Task<Result<(ItemAccess Item, Currency Currency)>> OpenAsync(IEnvelopeStore envelopes, IItemRegistry items, ItemId id, CancellationToken ct)
    {
        var loaded = await ReadableAsync(envelopes, items, id, ct);
        if (!loaded.TryGetValue(out var found))
            return loaded;
        return EnvelopeCheck.RequireOpen(found.Item).TryGetFailure(out var failure)
            ? Result.Failure<(ItemAccess, Currency)>(failure!) : loaded;
    }

    public static async Task<Result<(ItemAccess Item, Currency Currency)>> ReadableAsync(IEnvelopeStore envelopes, IItemRegistry items, ItemId id, CancellationToken ct)
    {
        var loaded = await EnvelopeCheck.LoadAsync(envelopes, items, id, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<(ItemAccess, Currency)>(failure!);
        loaded.TryGetValue(out var envelope);
        return Currency.TryCreate(envelope.Item.Currency, out var currency)
            ? Result.Success((envelope.Item, currency))
            : throw new InvalidOperationException($"Envelope {id} has no currency.");
    }

    /// <summary>The envelope's valuation dated exactly <paramref name="on"/>, if any.</summary>
    public static async Task<decimal?> ValuationOnAsync(IValuations valuations, ItemId id, LocalDate on, CancellationToken ct)
        => (await valuations.LatestAsync(id, on, ct)).TryGetValue(out var latest) && latest!.Value is { } v && v.AsOf == on ? v.Value.Amount : null;
}

[RequestHandler<AddHoldingCommand, Guid>]
public sealed class AddHoldingHandler(IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings, IUnitOfWork unitOfWork)
    : IRequestHandler<AddHoldingCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(AddHoldingCommand command, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        if ((await HoldingCheck.OpenAsync(envelopes, items, command.ItemId, ct)).TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        var checkedHolding = HoldingRules.Check(Guid.CreateVersion7(), command.ItemId, command.Holding, await holdings.HoldingsAsync(command.ItemId, ct));
        if (!checkedHolding.TryGetValue(out var holding))
            return Result.Failure<Guid>(checkedHolding.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());

        await holdings.AddAsync(holding, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(holding.Id);
    }
}

[RequestHandler<EditHoldingCommand, Guid>]
public sealed class EditHoldingHandler(IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings, IUnitOfWork unitOfWork)
    : IRequestHandler<EditHoldingCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(EditHoldingCommand command, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        if ((await HoldingCheck.OpenAsync(envelopes, items, command.ItemId, ct)).TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        var all = await holdings.HoldingsAsync(command.ItemId, ct);
        var stored = all.FirstOrDefault(h => h.Holding.Id == command.Holding);
        if (stored is null)
            return Result.Failure<Guid>(new NotFoundFailure("holding", command.Holding.ToString()));
        if (stored.Version != command.Version)
            return Result.Failure<Guid>(HoldingRules.Conflict());
        var checkedHolding = HoldingRules.Check(command.Holding, command.ItemId, command.Values, all);
        if (!checkedHolding.TryGetValue(out var holding))
            return Result.Failure<Guid>(checkedHolding.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());

        await holdings.ReplaceAsync(holding, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(command.Holding);
    }
}

[RequestHandler<DeleteHoldingCommand, bool>]
public sealed class DeleteHoldingHandler(IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings, IUnitOfWork unitOfWork)
    : IRequestHandler<DeleteHoldingCommand, bool>
{
    public async ValueTask<Result<bool>> HandleAsync(DeleteHoldingCommand command, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        if ((await HoldingCheck.OpenAsync(envelopes, items, command.ItemId, ct)).TryGetFailure(out var failure))
            return Result.Failure<bool>(failure!);
        var stored = (await holdings.HoldingsAsync(command.ItemId, ct)).FirstOrDefault(h => h.Holding.Id == command.Holding);
        if (stored is null)
            return Result.Failure<bool>(new NotFoundFailure("holding", command.Holding.ToString()));
        if (stored.Version != command.Version)
            return Result.Failure<bool>(HoldingRules.Conflict());
        if (await holdings.InUseAsync(command.Holding, ct))
            return Result.Failure<bool>(new Failure(InvestmentsCodes.HoldingInUse, "This holding is in a snapshot or has movements or cost reports: close it instead.", null));

        await holdings.DeleteAsync(command.Holding, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(true);
    }
}

[RequestHandler<RecordHoldingsSnapshotCommand, SnapshotRecorded>]
public sealed class RecordHoldingsSnapshotHandler(
    IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings, IValuations valuations, IHouseholdCalendar calendar, IUnitOfWork unitOfWork)
    : IRequestHandler<RecordHoldingsSnapshotCommand, SnapshotRecorded>
{
    public async ValueTask<Result<SnapshotRecorded>> HandleAsync(RecordHoldingsSnapshotCommand command, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var open = await HoldingCheck.OpenAsync(envelopes, items, command.ItemId, ct);
        if (!open.TryGetValue(out var envelope))
            return Result.Failure<SnapshotRecorded>(open.TryGetFailure(out var refused) ? refused! : throw new InvalidOperationException());

        var existing = await holdings.SnapshotsAsync(command.ItemId, ct);
        var sameDay = existing.FirstOrDefault(s => s.AsOf == command.Snapshot.AsOf);
        Guid id;
        if (command.SnapshotId is { } replaced)
        {
            if (existing.All(s => s.Id != replaced))
                return Result.Failure<SnapshotRecorded>(new NotFoundFailure("holding snapshot", replaced.ToString()));
            if (sameDay is not null && sameDay.Id != replaced)
                return Result.Failure<SnapshotRecorded>(new Failure(InvestmentsCodes.SnapshotExists, "Another snapshot has this date.",
                    new Dictionary<string, object?> { ["field"] = "asOf" }));
            id = replaced;
        }
        else
            id = sameDay?.Id ?? Guid.CreateVersion7(); // spec D5: a date's snapshot is replaced

        var checkedSnapshot = HoldingRules.Check(id, command.ItemId, command.Snapshot, await holdings.HoldingsAsync(command.ItemId, ct),
            envelope.Currency, await calendar.TodayAsync(ct));
        if (!checkedSnapshot.TryGetValue(out var snapshot))
            return Result.Failure<SnapshotRecorded>(checkedSnapshot.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());

        await holdings.SaveSnapshotAsync(snapshot, ct);
        if (command.Snapshot.RecordValuation)
        {
            var total = Money.Create(HoldingMath.Total(snapshot), envelope.Currency).TryGetValue(out var m) ? m
                : throw new InvalidOperationException("A sum of four-decimal amounts has four decimals.");
            var recorded = await valuations.RecordAsync(new NewValuation(command.ItemId, snapshot.AsOf, total, snapshot.SourceLabel), ct);
            if (recorded.TryGetFailure(out var failure))
                return Result.Failure<SnapshotRecorded>(failure!);
        }

        var warnings = HoldingMath.Warnings(snapshot, await HoldingCheck.ValuationOnAsync(valuations, command.ItemId, snapshot.AsOf, ct));
        await transaction.CommitAsync(ct);
        return Result.Success(new SnapshotRecorded(id, warnings.Select(HoldingWarningView.Of).ToList()));
    }
}

[RequestHandler<DeleteHoldingsSnapshotCommand, bool>]
public sealed class DeleteHoldingsSnapshotHandler(IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings, IUnitOfWork unitOfWork)
    : IRequestHandler<DeleteHoldingsSnapshotCommand, bool>
{
    public async ValueTask<Result<bool>> HandleAsync(DeleteHoldingsSnapshotCommand command, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        if ((await HoldingCheck.OpenAsync(envelopes, items, command.ItemId, ct)).TryGetFailure(out var failure))
            return Result.Failure<bool>(failure!);
        if ((await holdings.SnapshotsAsync(command.ItemId, ct)).All(s => s.Id != command.Snapshot))
            return Result.Failure<bool>(new NotFoundFailure("holding snapshot", command.Snapshot.ToString()));

        await holdings.DeleteSnapshotAsync(command.Snapshot, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(true);
    }
}

[RequestHandler<GetHoldingsQuery, HoldingsView>]
public sealed class GetHoldingsHandler(
    IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings, IFlows flows, IValuations valuations, IHouseholdCalendar calendar)
    : IRequestHandler<GetHoldingsQuery, HoldingsView>
{
    public async ValueTask<Result<HoldingsView>> HandleAsync(GetHoldingsQuery query, CancellationToken ct = default)
    {
        var loaded = await MovementFacts.LoadAsync(envelopes, items, holdings, flows, query.ItemId, write: false, null, ct);
        if (!loaded.TryGetValue(out var facts))
            return Result.Failure<HoldingsView>(loaded.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        var on = query.On ?? await calendar.TodayAsync(ct);
        var snapshots = await holdings.SnapshotsAsync(query.ItemId, ct);
        var snapshot = snapshots.FirstOrDefault(s => s.AsOf <= on);
        var currency = facts!.Currency;
        var movementWarnings = HoldingMath.Warnings(facts.Movements.Where(m => m.OccurredOn <= on).ToList(), facts.Flows)
            .Concat(HoldingMath.UnexplainedUnits(snapshots.Where(s => s.AsOf <= on).ToList(), facts.Movements, currency)).ToList();
        var floor = (await envelopes.FindAsync(query.ItemId, ct))!.UcShareFloorPct;
        HoldingFigures? Figures(Guid h) => HoldingMath.At(h, snapshots, facts.Movements, currency, on);
        if (snapshot is null)
            return Result.Success(new HoldingsView(query.ItemId, currency.Code, null, null, null, null, null, null, null, floor,
                facts.Holdings.Select(h => HoldingView.Of(h, null, null)).ToList(), movementWarnings.Select(HoldingWarningView.Of).ToList()));

        var valuation = await HoldingCheck.ValuationOnAsync(valuations, query.ItemId, snapshot.AsOf, ct);
        var total = HoldingMath.Total(snapshot);
        var ucShare = HoldingMath.UcSharePct(snapshot, facts.Holdings.ToDictionary(h => h.Holding.Id, h => h.Holding.Kind));
        var shareWarning = ucShare < floor ? [new HoldingWarning(HoldingWarnings.UcShareBelowFloor, null, snapshot.AsOf)] : Array.Empty<HoldingWarning>();
        return Result.Success(new HoldingsView(query.ItemId, currency.Code, snapshot.AsOf, snapshot.Id, snapshot.Complete,
            AmountView.From(Amounts.Of(total, currency)), valuation is { } v ? AmountView.From(Amounts.Of(v - total, currency)) : null,
            AmountView.From(Amounts.Of(HoldingMath.ExplicitFees(facts.Movements, snapshot.AsOf), currency)), ucShare, floor,
            facts.Holdings.Select(h => HoldingView.Of(h, snapshot.Lines.FirstOrDefault(l => l.HoldingId == h.Holding.Id) is { } line
                ? LineView.Of(line, HoldingMath.Weight(snapshot, line), currency) : null, Figures(h.Holding.Id))).ToList(),
            HoldingMath.Warnings(snapshot, valuation).Concat(shareWarning).Concat(movementWarnings).Select(HoldingWarningView.Of).ToList()));
    }
}

[RequestHandler<ListHoldingsSnapshotsQuery, HoldingSnapshotList>]
public sealed class ListHoldingsSnapshotsHandler(IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings)
    : IRequestHandler<ListHoldingsSnapshotsQuery, HoldingSnapshotList>
{
    public async ValueTask<Result<HoldingSnapshotList>> HandleAsync(ListHoldingsSnapshotsQuery query, CancellationToken ct = default)
    {
        var readable = await HoldingCheck.ReadableAsync(envelopes, items, query.ItemId, ct);
        if (!readable.TryGetValue(out var envelope))
            return Result.Failure<HoldingSnapshotList>(readable.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        var currency = envelope.Currency;
        return Result.Success(new HoldingSnapshotList(query.ItemId, (await holdings.SnapshotsAsync(query.ItemId, ct))
            .Select(s => new SnapshotView(s.Id, s.AsOf, s.SourceLabel, s.Complete, AmountView.From(Amounts.Of(HoldingMath.Total(s), currency)),
                s.Lines.Select(l => new SnapshotLineView(l.HoldingId, Units6.Of(l.Units), Units6.Of(l.UnitPrice), AmountView.From(Amounts.Of(l.Value, currency)), Units6.Of(l.CostBasisPerUnit))).ToList()))
            .ToList()));
    }
}

internal static class Amounts
{
    public static Money Of(decimal amount, Currency currency)
        => Money.Create(amount, currency).TryGetValue(out var money) ? money
            : throw new InvalidOperationException("A sum of four-decimal amounts has four decimals.");
}

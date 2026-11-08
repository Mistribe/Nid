using System.Globalization;
using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.Income.Contracts;
using NodaTime.Text;

namespace Nid.Income.Application;

/// <summary>Documents tranche 2 design §3: a phase and a one-off income as link targets, both anchored on their source.</summary>
public sealed class IncomeDocumentTargets(IPhaseStore phases, IFlows flows, IItemRegistry items) : IDocumentTargets
{
    public const string PhaseKind = "income.phase";
    public const string OneOffKind = "income.one_off";

    private static readonly LocalDatePattern Day = LocalDatePattern.Create("dd/MM/yyyy", CultureInfo.InvariantCulture);

    public IReadOnlySet<string> Kinds { get; } = new HashSet<string> { PhaseKind, OneOffKind };

    public async Task<IReadOnlyDictionary<Guid, ResolvedTarget>> ResolveAsync(string kind, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        switch (kind)
        {
            case PhaseKind:
                var found = await phases.FindManyAsync(ids, ct);
                var sources = await NamesAsync(found.Select(p => p.Source), ct);
                return found.Where(p => sources.ContainsKey(p.Source)).ToDictionary(p => p.Id, p => new ResolvedTarget(p.Source.Value,
                    string.IsNullOrWhiteSpace(p.Note) ? $"{sources[p.Source]} · {Day.Format(p.StartsOn)}" : p.Note));
            case OneOffKind:
                // Any other flow (an envelope's income, a contribution, another category) is not a one-off target.
                var oneOffs = (await flows.FindManyAsync(ids, ct)).Where(f => f.Kind == FlowKind.Income && f.Category == IncomeWire.OneOffCategory).ToList();
                var names = await NamesAsync(oneOffs.Select(f => f.Item), ct, sourcesOnly: true);
                return oneOffs.Where(f => names.ContainsKey(f.Item)).ToDictionary(f => f.Id, f => new ResolvedTarget(f.Item.Value,
                    $"{names[f.Item]} · {Day.Format(f.OccurredOn)} · revenu exceptionnel"));
            default:
                return new Dictionary<Guid, ResolvedTarget>();
        }
    }

    private async Task<IReadOnlyDictionary<ItemId, string>> NamesAsync(IEnumerable<ItemId> sources, CancellationToken ct, bool sourcesOnly = false)
    {
        var distinct = sources.Distinct().ToList();
        return distinct.Count == 0
            ? new Dictionary<ItemId, string>()
            : (await items.FindManyAsync(distinct, ct)).Where(p => !sourcesOnly || p.Value is { Module: "income", Kind: "source" }).ToDictionary(p => p.Key, p => p.Value.DisplayName);
    }
}

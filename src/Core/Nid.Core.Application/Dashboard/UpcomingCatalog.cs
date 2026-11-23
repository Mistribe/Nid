using Nid.Core.Contracts;

namespace Nid.Core.Application.Dashboard;

/// <summary>Issue #514: every event kind the modules declare (<see cref="IUpcomingKinds"/>), by name. A kind declared twice is a wiring bug and throws.</summary>
public sealed class UpcomingCatalog(IEnumerable<IUpcomingKinds> modules)
{
    public IReadOnlyDictionary<string, UpcomingKind> Kinds { get; } = Index(modules);

    /// <summary>Issue #240: a kind that happens once; an unknown kind is not one.</summary>
    public bool IsMilestone(string kind) => Kinds.TryGetValue(kind, out var k) && k.Cadence == UpcomingCadence.Milestone;

    /// <summary>What the calendar feed says of an event; null for an unknown kind.</summary>
    public string? Summary(string kind, string? label) => Kinds.TryGetValue(kind, out var k) ? k.Summary(label) : null;

    private static Dictionary<string, UpcomingKind> Index(IEnumerable<IUpcomingKinds> modules)
    {
        var kinds = new Dictionary<string, UpcomingKind>(StringComparer.Ordinal);
        foreach (var kind in modules.SelectMany(m => m.Kinds))
            if (!kinds.TryAdd(kind.Kind, kind))
                throw new InvalidOperationException($"The event kind '{kind.Kind}' is declared twice.");
        return kinds;
    }
}

using Nid.Platform.Contracts;

namespace Nid.Platform.Notifications;

/// <summary>
/// Every kind the modules declare (notifications design §2.2), by name. Scoped, not a singleton: the sources depend on scoped
/// stores, so a singleton would capture scoped instances. A kind declared twice is a wiring bug and throws.
/// </summary>
public sealed class NotificationCatalog(IEnumerable<INotificationSource> sources)
{
    public IReadOnlyDictionary<string, NotificationKind> Kinds { get; } = Index(sources);

    public NotificationKind? Find(string kind) => Kinds.GetValueOrDefault(kind);

    private static Dictionary<string, NotificationKind> Index(IEnumerable<INotificationSource> sources)
    {
        var kinds = new Dictionary<string, NotificationKind>();
        foreach (var source in sources)
        foreach (var kind in source.Kinds)
            if (!kinds.TryAdd(kind.Kind, kind))
                throw new InvalidOperationException($"The notification kind '{kind.Kind}' is declared twice (source '{source.Name}').");
        return kinds;
    }
}

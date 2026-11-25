using Nid.Platform.Contracts;

namespace Nid.Core.Application;

/// <summary>#644: Core's kinds published through <see cref="INotifier"/>, the proposals' (#384).</summary>
public sealed class CoreNotificationKinds : IPublishedNotificationKinds
{
    public IReadOnlyList<NotificationKind> Kinds { get; } =
    [
        new("proposal_pending", NotificationFamilies.Action, NotificationLevel.Action, "core", null)
        {
            Personal = true,
            Template = new("Une proposition attend votre décision") { Notice = true, Body = _ => "Une décision est attendue", Link = Proposal },
        },
        // #384: the other adult refused to make an item private; told to the owner who asked.
        new("private_request_refused", NotificationFamilies.Action, NotificationLevel.Info, "core", null)
        {
            Personal = true,
            Template = new("Votre demande de passage en privé a été refusée")
            {
                Notice = true, Body = _ => "L'autre adulte n'a pas accepté : l'élément reste partagé.", Link = Proposal,
            },
        },
    ];

    private static string Proposal(NotificationFacts f) => $"/proposals/{f.Key[(f.Key.LastIndexOf(':') + 1)..]}";
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Notifications;
using NodaTime;

namespace Nid.Platform.Infrastructure.Notifications;

/// <summary>
/// <see cref="INotifier"/> in a scope of its own, so on a connection and a transaction of its own (spec U10, U11): the notice of a
/// request that fails and rolls back still lands. A failure is logged at Error and never reaches the caller.
/// </summary>
public sealed partial class Notifier(IServiceScopeFactory scopes, IClock clock, ILogger<Notifier> logger) : INotifier
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <remarks>
    /// <paramref name="ct"/> is accepted for the interface's shape only and never reaches the writes (spec U10): a request whose client
    /// disconnected must not lose the notice that disconnection caused. The writes run on their own bound instead.
    /// </remarks>
    public async Task PublishAsync(Notice notice, CancellationToken ct)
    {
        using var bound = new CancellationTokenSource(Bound);
        var own = bound.Token;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            if (sp.GetRequiredService<NotificationCatalog>().Find(notice.Kind) is not { } kind)
            {
                LogUnknownKind(notice.Kind);
                return;
            }

            var persons = sp.GetRequiredService<IPersonRegistry>();
            var store = sp.GetRequiredService<INotificationStore>();
            var outbox = sp.GetRequiredService<IDeliveryOutbox>();
            var recipients = (await persons.ListAsync(own)).Where(p => p is { Kind: PersonKind.Adult, IsPending: false } && Reaches(notice.Recipients, p)).ToList();

            await using var transaction = await sp.GetRequiredService<IUnitOfWork>().BeginAsync(own);
            var now = clock.GetCurrentInstant(); // one publication instant for every recipient
            foreach (var person in recipients)
            {
                // The link of a notice may depend on the reader (the security journal is for admins).
                var forPerson = person.IsAdmin ? notice with { Params = new Dictionary<string, string>(notice.Params) { ["admin"] = "1" } } : notice;
                if (await store.PublishAsync(person.Id, kind, forPerson, now, own) is { } opened)
                    await outbox.EnqueueAsync(person.Id, [opened], await store.PreferencesAsync(person.Id, own), own);
            }

            await transaction.CommitAsync(own);
        }
        catch (OperationCanceledException)
        {
            LogCancelled(notice.Kind);
        }
        catch (Exception e)
        {
            LogFailed(e, notice.Kind);
        }
    }

    public async Task ResolveAsync(string key, CancellationToken ct)
    {
        using var bound = new CancellationTokenSource(Bound);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<INotificationStore>().ResolveKeyAsync(key, bound.Token);
        }
        catch (OperationCanceledException)
        {
            LogCancelled("resolve");
        }
        catch (Exception e)
        {
            LogResolveFailed(e);
        }
    }

    private static bool Reaches(Recipients to, PersonInfo person) => to switch
    {
        Recipients.People people => people.Ids.Contains(person.Id),
        Recipients.Admins => person.IsAdmin,
        _ => true,
    };

    // Only the notifier's own bound cancels here (the caller's token never reaches the writes): a lost notice, so an error (U11).
    [LoggerMessage(Level = LogLevel.Error, Message = "The {Kind} notice timed out before it was written")]
    private partial void LogCancelled(string kind);

    [LoggerMessage(Level = LogLevel.Error, Message = "The {Kind} notice could not be published")]
    private partial void LogFailed(Exception exception, string kind);

    [LoggerMessage(Level = LogLevel.Error, Message = "A notice could not be resolved")]
    private partial void LogResolveFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The notice kind {Kind} is not in the catalogue; ignored")]
    private partial void LogUnknownKind(string kind);
}

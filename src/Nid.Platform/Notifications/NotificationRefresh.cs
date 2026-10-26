using Nid.Core.Contracts;

namespace Nid.Platform.Notifications;

/// <summary>Asks for a person's notifications to be evaluated soon, after a page load (notifications design §3.3).</summary>
public interface INotificationRefresh
{
    void Request(PersonId person);
}

/// <summary>The refresh when no job runs (tests, the OpenAPI generation, the admin CLI): nothing is evaluated.</summary>
public sealed class NoNotificationRefresh : INotificationRefresh
{
    public void Request(PersonId person)
    {
    }
}

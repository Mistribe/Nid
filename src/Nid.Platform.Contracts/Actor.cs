using Nid.Core.Contracts;

namespace Nid.Platform.Contracts;

public enum ActorKind
{
    Person,
    System,
}

public enum ActorOrigin
{
    Ui,
    Api,
    Mcp,
    Job,
    Import,
}

public static class ScopeNames
{
    public const string Read = "read";
    public const string Write = "write";
    public const string WriteDirect = "write:direct";
}

public sealed record Actor(ActorKind Kind, PersonId? PersonId, ActorOrigin Via, IReadOnlySet<string> Scopes)
{
    private static readonly IReadOnlySet<string> AllScopes =
        new HashSet<string> { ScopeNames.Read, ScopeNames.Write, ScopeNames.WriteDirect };

    public static Actor Browser(PersonId person)
        => new(ActorKind.Person, person, ActorOrigin.Ui, AllScopes);

    public static Actor System { get; } = new(ActorKind.System, null, ActorOrigin.Job, AllScopes);

    /// <summary><c>write:direct</c> includes <c>write</c>, which includes <c>read</c>.</summary>
    public bool Has(string scope) => scope switch
    {
        ScopeNames.Read => Scopes.Overlaps(AllScopes),
        ScopeNames.Write => Scopes.Contains(ScopeNames.Write) || Scopes.Contains(ScopeNames.WriteDirect),
        _ => Scopes.Contains(scope),
    };
}

/// <summary>Scoped holder set once per request by the entry point (HTTP, MCP, job); handlers never read HttpContext.</summary>
public interface IActorAccessor
{
    Actor? Current { get; set; }
}

public sealed class ActorAccessor : IActorAccessor
{
    public Actor? Current { get; set; }
}

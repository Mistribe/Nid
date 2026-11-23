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
    AdminCli,

    /// <summary>#497: the in-app assistant, acting for the signed-in person (<c>docs/ai/README.md</c> §8.1, decision L).</summary>
    Assistant,
}

public static class ActorOriginWire
{
    /// <summary>The value `via` is written with (1F3a spec §3; 1F4 design §2.2).</summary>
    public static string ToWire(this ActorOrigin origin) => origin switch
    {
        ActorOrigin.Ui => "ui",
        ActorOrigin.Api => "api",
        ActorOrigin.Mcp => "mcp",
        ActorOrigin.Job => "job",
        ActorOrigin.Import => "import",
        ActorOrigin.AdminCli => "admin-cli",
        ActorOrigin.Assistant => "assistant",
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, null),
    };
}

public static class ScopeNames
{
    public const string Read = "read";
    public const string Write = "write";
    public const string WriteDirect = "write:direct";

    /// <summary>Lent only to a proposal's dry run (<c>RouteReplay</c>); no token preset holds it.</summary>
    public const string DryRun = "dry-run";
}

public sealed record Actor(ActorKind Kind, PersonId? PersonId, ActorOrigin Via, IReadOnlySet<string> Scopes)
{
    private static readonly IReadOnlySet<string> AllScopes =
        new HashSet<string> { ScopeNames.Read, ScopeNames.Write, ScopeNames.WriteDirect };

    /// <summary>
    /// The technical role (administration-and-audit.md §2). Carried by a browser session of an admin and by the
    /// system; never by a token, even its owner's. It opens no item: Readers and Writers ignore it.
    /// </summary>
    public bool IsAdmin { get; init; }

    /// <summary>4A: the personal access token this actor authenticated with; null for a session, the system and the CLI.</summary>
    public Guid? TokenId { get; init; }

    /// <summary>
    /// #533: the assistant whose turn goes off the premises (decision C, <c>docs/ai/README.md</c> §6.3, §6.4): its person
    /// consented for their own items by turning it on, but an item one of whose owners chose <c>never</c> is held back. Null on
    /// the premises.
    /// </summary>
    public OffsiteReading? Offsite { get; init; }

    /// <summary>A bearer token (tokens-and-credentials.md §1.4): its person, restricted to its scopes, never admin.</summary>
    public static Actor Token(PersonId person, Guid tokenId, IReadOnlySet<string> scopes, ActorOrigin via)
        => new(ActorKind.Person, person, via, scopes) { TokenId = tokenId };

    /// <summary>Who a refused authentication is recorded as (spec 4A §5): nobody known, no scope, no role.</summary>
    public static Actor Unidentified(ActorOrigin via) => new(ActorKind.System, null, via, new HashSet<string>());

    public static Actor Browser(PersonId person, bool isAdmin = false)
        => new(ActorKind.Person, person, ActorOrigin.Ui, AllScopes) { IsAdmin = isAdmin };

    /// <summary>
    /// The in-app assistant for <paramref name="person"/> (#497, decision L): it reads what its person reads and proposes,
    /// never applies (<c>write</c>, not <c>write:direct</c>), never admin, and never runs a browser-only command.
    /// <paramref name="offsite"/>: see <see cref="Offsite"/>.
    /// </summary>
    public static Actor Assistant(PersonId person, OffsiteReading? offsite = null)
        => new(ActorKind.Person, person, ActorOrigin.Assistant, new HashSet<string> { ScopeNames.Read, ScopeNames.Write }) { Offsite = offsite };

    /// <summary>The notification evaluation reading as that adult: read only, via the job.</summary>
    public static Actor Notifier(PersonId person) => new(ActorKind.Person, person, ActorOrigin.Job, new HashSet<string> { ScopeNames.Read });

    /// <summary>Bank sync (D22): the job proposing or writing a bank balance as the creator of the connection that read it.</summary>
    public static Actor BankSync(PersonId person)
        => new(ActorKind.Person, person, ActorOrigin.Job, new HashSet<string> { ScopeNames.Read, ScopeNames.Write });

    public static Actor System { get; } = new(ActorKind.System, null, ActorOrigin.Job, AllScopes) { IsAdmin = true };

    /// <summary>`nid admin` (1F4 design §2.2): the system, run by the hoster from the container.</summary>
    public static Actor Cli { get; } = new(ActorKind.System, null, ActorOrigin.AdminCli, AllScopes) { IsAdmin = true };

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

/// <summary>#533: who chose <c>never</c> for their data off the premises (§6.3): every item one of them owns is held back.</summary>
public sealed record OffsiteReading(IReadOnlySet<PersonId> Never)
{
    /// <summary>Whether an item with these owners is held back off the premises.</summary>
    public bool Holds(IEnumerable<PersonId> owners) => owners.Any(Never.Contains);
}

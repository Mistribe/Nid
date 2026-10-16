using Nid.Platform.Contracts;

namespace Nid.Platform.Tests;

/// <summary>1F4 design §2.2: `via` is written as the wire names, `admin-cli` included.</summary>
public class ActorOriginTests
{
    [Theory]
    [InlineData(ActorOrigin.Ui, "ui")]
    [InlineData(ActorOrigin.Api, "api")]
    [InlineData(ActorOrigin.Mcp, "mcp")]
    [InlineData(ActorOrigin.Job, "job")]
    [InlineData(ActorOrigin.Import, "import")]
    [InlineData(ActorOrigin.AdminCli, "admin-cli")]
    public void Each_origin_has_its_wire_name(ActorOrigin origin, string wire) => Assert.Equal(wire, origin.ToWire());

    [Fact]
    public void The_cli_actor_is_the_system_with_its_own_origin_and_the_admin_role()
    {
        Assert.Equal((ActorKind.System, ActorOrigin.AdminCli, true), (Actor.Cli.Kind, Actor.Cli.Via, Actor.Cli.IsAdmin));
        Assert.Null(Actor.Cli.PersonId);
    }

    [Fact]
    public void The_admin_journal_shows_what_the_cli_did()
    {
        Assert.Contains(SecurityEventNames.AdminCommand, SecurityEventNames.AdminJournal);
        Assert.Contains(SecurityEventNames.SubjectRebound, SecurityEventNames.AdminJournal);
    }

    [Fact]
    public void A_token_actor_is_its_person_with_its_scopes_and_never_admin()
    {
        var person = new Nid.Core.Contracts.PersonId(Guid.NewGuid());
        var id = Guid.NewGuid();

        var actor = Actor.Token(person, id, new HashSet<string> { ScopeNames.Read }, ActorOrigin.Api);

        Assert.Equal((ActorKind.Person, person, ActorOrigin.Api, id, false), (actor.Kind, actor.PersonId!.Value, actor.Via, actor.TokenId!.Value, actor.IsAdmin));
        Assert.True(actor.Has(ScopeNames.Read));
        Assert.False(actor.Has(ScopeNames.Write));
        Assert.Null(Actor.Browser(person).TokenId);
    }

    [Fact]
    public void An_unidentified_actor_has_no_person_no_scope_and_no_role()
    {
        var actor = Actor.Unidentified(ActorOrigin.Mcp);
        Assert.Equal((ActorKind.System, ActorOrigin.Mcp, false), (actor.Kind, actor.Via, actor.IsAdmin));
        Assert.Null(actor.PersonId);
        Assert.False(actor.Has(ScopeNames.Read));
    }

    [Fact]
    public void A_failed_token_authentication_is_in_the_admin_journal_but_a_revocation_is_not()
    {
        Assert.Contains(SecurityEventNames.TokenAuthFailed, SecurityEventNames.AdminJournal);
        Assert.DoesNotContain(SecurityEventNames.TokenRevoked, SecurityEventNames.AdminJournal);
    }
}

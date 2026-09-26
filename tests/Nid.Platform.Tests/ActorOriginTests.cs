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
}

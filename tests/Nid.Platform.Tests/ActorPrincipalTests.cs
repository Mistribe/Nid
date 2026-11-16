using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Authorization;

namespace Nid.Platform.Tests;

/// <summary>The actor as Synapse's checks read it: the scope ladder, the admin role, the system, and Nid's refusals.</summary>
public class ActorPrincipalTests
{
    private static ActorPrincipal Of(Actor? actor) => new(new ActorAccessor { Current = actor });

    private static Actor Token(params string[] scopes)
        => Actor.Token(PersonId.New(), Guid.NewGuid(), new HashSet<string>(scopes), ActorOrigin.Api);

    [Fact]
    public void No_actor_is_unauthenticated_and_holds_nothing()
    {
        var principal = Of(null);

        Assert.False(principal.IsAuthenticated);
        Assert.False(principal.HasScope(ScopeNames.Read));
        Assert.False(principal.IsInRole(Roles.Admin));
        Assert.Null(principal.Id);
    }

    [Fact]
    public void Write_direct_includes_write_which_includes_read()
    {
        var principal = Of(Token(ScopeNames.WriteDirect));

        Assert.True(principal.HasScope(ScopeNames.Read));
        Assert.True(principal.HasScope(ScopeNames.Write));
        Assert.True(principal.HasScope(ScopeNames.WriteDirect));
        Assert.False(principal.HasScope(ScopeNames.DryRun));
    }

    [Fact]
    public void Write_alone_does_not_give_write_direct()
        => Assert.False(Of(Token(ScopeNames.Read, ScopeNames.Write)).HasScope(ScopeNames.WriteDirect));

    [Fact]
    public void Only_an_admin_session_and_the_system_are_in_the_admin_role()
    {
        Assert.True(Of(Actor.Browser(PersonId.New(), isAdmin: true)).IsInRole(Roles.Admin));
        Assert.True(Of(Actor.System).IsInRole(Roles.Admin));
        Assert.False(Of(Actor.Browser(PersonId.New())).IsInRole(Roles.Admin));
        Assert.False(Of(Token(ScopeNames.WriteDirect)).IsInRole(Roles.Admin));
        Assert.False(Of(Actor.Browser(PersonId.New(), isAdmin: true)).IsInRole("other"));
    }

    [Fact]
    public void The_system_and_the_cli_are_system_principals_and_a_person_is_not()
    {
        Assert.True(Of(Actor.System).IsSystem);
        Assert.True(Of(Actor.Cli).IsSystem);
        Assert.False(Of(Actor.Browser(PersonId.New())).IsSystem);
        Assert.False(Of(Actor.BankSync(PersonId.New())).IsSystem);
    }

    [Fact]
    public void A_person_is_identified_by_their_id()
    {
        var person = PersonId.New();

        Assert.Equal(person.Value.ToString(), Of(Actor.Browser(person)).Id);
    }

    [Fact]
    public void A_missing_role_is_refused_with_the_admin_code()
    {
        var failure = NidAuthorization.DenialFailure(Denial(AuthorizationDenialReason.MissingRole, Roles.Admin));

        var refused = Assert.IsType<UnauthorizedFailure>(failure);
        Assert.Equal(AdminCodes.Required, refused.Code);
    }

    [Fact]
    public void A_missing_scope_names_the_scope()
    {
        var failure = NidAuthorization.DenialFailure(Denial(AuthorizationDenialReason.MissingScope, ScopeNames.WriteDirect));

        Assert.IsType<UnauthorizedFailure>(failure);
        Assert.Equal("The 'write:direct' scope is required.", failure!.Message);
    }

    [Fact]
    public void No_actor_is_refused_as_unauthenticated()
        => Assert.IsType<UnauthenticatedFailure>(NidAuthorization.DenialFailure(Denial(AuthorizationDenialReason.Unauthenticated, null)));

    private static AuthorizationDenial Denial(AuthorizationDenialReason reason, string? requirement)
        => new(new object(), reason, null, requirement, null);
}

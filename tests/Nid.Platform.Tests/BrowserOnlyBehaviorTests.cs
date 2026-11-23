using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Tests;

/// <summary>Spec #23: a command a token never runs for real, only as a proposal's dry run.</summary>
public class BrowserOnlyBehaviorTests
{
    private sealed record DeleteCommand : ICommand<int>, IConfirmedInBrowser;

    private static async Task<(bool Ran, string? Code)> RunAsync(Actor actor)
    {
        var ran = false;
        var behavior = new BrowserOnlyBehavior<DeleteCommand, int>(new ActorAccessor { Current = actor });
        var result = await behavior.HandleAsync(new DeleteCommand(), (_, _) =>
        {
            ran = true;
            return ValueTask.FromResult(Result.Success(1));
        });
        return (ran, result.TryGetFailure(out var failure) ? failure.Code : null);
    }

    private static Actor Token(params string[] scopes)
        => Actor.Token(PersonId.New(), Guid.NewGuid(), new HashSet<string>(scopes), ActorOrigin.Api);

    [Fact]
    public async Task Token_with_write_direct_is_refused_a_confirmed_in_browser_command()
        => Assert.Equal((false, BrowserOnlyCodes.Required), await RunAsync(Token(ScopeNames.Read, ScopeNames.Write, ScopeNames.WriteDirect)));

    [Fact]
    public async Task Token_in_a_dry_run_runs_it()
        => Assert.Equal((true, null), await RunAsync(Token(ScopeNames.WriteDirect, ScopeNames.DryRun)));

    [Fact]
    public async Task Browser_session_runs_it()
        => Assert.Equal((true, null), await RunAsync(Actor.Browser(PersonId.New())));

    /// <summary>#497: the assistant acts as its person, never as the browser itself (AST-7).</summary>
    [Fact]
    public async Task The_assistant_is_refused_a_confirmed_in_browser_command()
        => Assert.Equal((false, BrowserOnlyCodes.Required), await RunAsync(Actor.Assistant(PersonId.New())));

    [Fact]
    public async Task The_assistant_in_a_dry_run_runs_it()
    {
        var assistant = Actor.Assistant(PersonId.New());

        Assert.Equal((true, null), await RunAsync(assistant with { Scopes = new HashSet<string>(assistant.Scopes) { ScopeNames.DryRun } }));
    }

    [Fact]
    public void The_assistant_reads_and_proposes_as_its_person_never_more()
    {
        var person = PersonId.New();
        var assistant = Actor.Assistant(person);

        Assert.Equal((ActorKind.Person, person, "assistant", false), (assistant.Kind, assistant.PersonId, assistant.Via.ToWire(), assistant.IsAdmin));
        Assert.Equal(new HashSet<string> { ScopeNames.Read, ScopeNames.Write }, assistant.Scopes);
        Assert.Null(assistant.TokenId);
    }
}

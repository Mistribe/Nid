using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Tests;

/// <summary>Spec #23: a command a token never runs for real, only as a proposal's dry run.</summary>
public class ScopeBehaviorTests
{
    private sealed record DeleteCommand : ICommand<int>, IConfirmedInBrowser;

    private static async Task<(bool Ran, string? Code)> RunAsync(Actor actor)
    {
        var ran = false;
        var behavior = new ScopeBehavior<DeleteCommand, int>(new ActorAccessor { Current = actor });
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
}

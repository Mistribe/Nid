using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Core.Domain;
using Nid.Core.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Core.IntegrationTests;

[Collection("postgres")]
public class PersonalAccessTokenStoreTests(PostgresFixture postgres)
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 16, 9, 0);

    private sealed class StepClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
        public void Advance(NodaTime.Duration by) => now += by;
    }

    private async Task<(string Connection, PersonId Adult)> SeedAsync()
    {
        var connection = await postgres.NewConnectionStringAsync();
        await using var db = PostgresFixture.NewCoreContext(connection);
        var id = Guid.CreateVersion7();
        db.Persons.Add(new PersonRow { Id = id, Kind = "adult", OidcSubject = "sub", DisplayName = "A", CreatedAt = Now });
        await db.SaveChangesAsync();
        return (connection, new PersonId(id));
    }

    private static PersonalAccessTokenStore Store(string connection, IClock? clock = null)
        => new(PostgresFixture.NewCoreContext(connection), clock ?? new StepClock(Now));

    [Fact]
    public async Task A_created_token_is_stored_hashed_and_listed_without_its_secret()
    {
        var (connection, adult) = await SeedAsync();

        var created = (await Store(connection).CreateAsync(adult, "  Claude sur mon laptop ", TokenPresets.Propose, null, TestContext.Current.CancellationToken)).Ok();

        Assert.True(TokenFormat.TryReadPrefix(created.Secret, out var prefix));
        Assert.Equal(prefix, created.Token.Prefix);
        Assert.Equal("Claude sur mon laptop", created.Token.Name);
        Assert.Equal(Now.Plus(NodaTime.Duration.FromDays(90)), created.Token.ExpiresAt);
        Assert.Equal(["read", "write"], created.Token.Scopes.Order(StringComparer.Ordinal));
        await using var db = PostgresFixture.NewCoreContext(connection);
        var row = await db.PersonalAccessTokens.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TokenFormat.Hash(created.Secret), row.TokenHash);
        Assert.DoesNotContain(created.Secret[17..], row.TokenHash);
        var listed = Assert.Single(await Store(connection).ListAsync(adult, TestContext.Current.CancellationToken));
        Assert.Equal(created.Token.Id, listed.Id);
    }

    [Theory]
    [InlineData("", TokenPresets.Read, 90, TokenCodes.NameInvalid)]
    [InlineData("x", "admin", 90, TokenCodes.PresetUnknown)]
    [InlineData("x", TokenPresets.Read, 0, TokenCodes.ExpiryInvalid)]
    [InlineData("x", TokenPresets.Read, 366, TokenCodes.ExpiryInvalid)]
    public async Task Invalid_input_is_refused_at_its_field(string name, string preset, int days, string code)
    {
        var (connection, adult) = await SeedAsync();

        var result = await Store(connection).CreateAsync(adult, name, preset, days, TestContext.Current.CancellationToken);

        Assert.Equal(code, result.Fails().Code);
    }

    [Fact]
    public async Task A_name_longer_than_80_characters_is_refused()
    {
        var (connection, adult) = await SeedAsync();
        Assert.Equal(TokenCodes.NameInvalid, (await Store(connection).CreateAsync(adult, new string('n', 81), TokenPresets.Read, 30, TestContext.Current.CancellationToken)).Fails().Code);
    }

    [Fact]
    public async Task An_eleventh_active_token_is_refused_but_a_revoked_one_frees_its_slot()
    {
        var (connection, adult) = await SeedAsync();
        var first = (await Store(connection).CreateAsync(adult, "t0", TokenPresets.Read, 30, TestContext.Current.CancellationToken)).Ok();
        for (var i = 1; i < 10; i++)
            (await Store(connection).CreateAsync(adult, $"t{i}", TokenPresets.Read, 30, TestContext.Current.CancellationToken)).Ok();

        Assert.Equal(TokenCodes.LimitReached, (await Store(connection).CreateAsync(adult, "t10", TokenPresets.Read, 30, TestContext.Current.CancellationToken)).Fails().Code);

        Assert.True((await Store(connection).RevokeAsync(adult, first.Token.Id, TestContext.Current.CancellationToken)).Ok());
        (await Store(connection).CreateAsync(adult, "t10", TokenPresets.Read, 30, TestContext.Current.CancellationToken)).Ok();
    }

    // Review Focus 3.
    [Fact]
    public async Task The_tenth_slot_goes_to_one_of_two_concurrent_creations()
    {
        var (connection, adult) = await SeedAsync();
        for (var i = 0; i < 9; i++)
            (await Store(connection).CreateAsync(adult, $"t{i}", TokenPresets.Read, 30, TestContext.Current.CancellationToken)).Ok();

        var results = await Task.WhenAll(
            Store(connection).CreateAsync(adult, "a", TokenPresets.Read, 30, TestContext.Current.CancellationToken),
            Store(connection).CreateAsync(adult, "b", TokenPresets.Read, 30, TestContext.Current.CancellationToken));

        Assert.Single(results, r => r.IsSuccess);
        Assert.Equal(TokenCodes.LimitReached, Assert.Single(results, r => !r.IsSuccess).Fails().Code);
    }

    [Fact]
    public async Task Revoking_is_idempotent_and_another_persons_token_is_not_found()
    {
        var (connection, adult) = await SeedAsync();
        var created = (await Store(connection).CreateAsync(adult, "t", TokenPresets.Read, 30, TestContext.Current.CancellationToken)).Ok();

        Assert.True((await Store(connection).RevokeAsync(adult, created.Token.Id, TestContext.Current.CancellationToken)).Ok());
        Assert.False((await Store(connection).RevokeAsync(adult, created.Token.Id, TestContext.Current.CancellationToken)).Ok());
        Assert.IsType<UnambitiousFx.Functional.Failures.NotFoundFailure>((await Store(connection).RevokeAsync(PersonId.New(), created.Token.Id, TestContext.Current.CancellationToken)).Fails());
    }

    [Fact]
    public async Task Revoke_all_revokes_the_active_ones_only()
    {
        var (connection, adult) = await SeedAsync();
        var revoked = (await Store(connection).CreateAsync(adult, "r", TokenPresets.Read, 30, TestContext.Current.CancellationToken)).Ok();
        await Store(connection).RevokeAsync(adult, revoked.Token.Id, TestContext.Current.CancellationToken);
        (await Store(connection).CreateAsync(adult, "a", TokenPresets.Read, 30, TestContext.Current.CancellationToken)).Ok();
        (await Store(connection).CreateAsync(adult, "b", TokenPresets.Read, 30, TestContext.Current.CancellationToken)).Ok();

        Assert.Equal(2, await Store(connection).RevokeAllAsync(adult, TestContext.Current.CancellationToken));
        Assert.All(await Store(connection).ListAsync(adult, TestContext.Current.CancellationToken), t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task Touch_writes_last_used_at_at_most_once_a_minute()
    {
        var (connection, adult) = await SeedAsync();
        var clock = new StepClock(Now);
        var created = (await Store(connection, clock).CreateAsync(adult, "t", TokenPresets.Read, 30, TestContext.Current.CancellationToken)).Ok();

        await Store(connection, clock).TouchAsync(created.Token.Id, TestContext.Current.CancellationToken);
        clock.Advance(NodaTime.Duration.FromSeconds(30));
        await Store(connection, clock).TouchAsync(created.Token.Id, TestContext.Current.CancellationToken);
        Assert.Equal(Now, (await Store(connection).ListAsync(adult, TestContext.Current.CancellationToken))[0].LastUsedAt);

        clock.Advance(NodaTime.Duration.FromSeconds(31));
        await Store(connection, clock).TouchAsync(created.Token.Id, TestContext.Current.CancellationToken);
        Assert.Equal(clock.GetCurrentInstant(), (await Store(connection).ListAsync(adult, TestContext.Current.CancellationToken))[0].LastUsedAt);
    }

    [Fact]
    public async Task The_credential_is_found_by_prefix()
    {
        var (connection, adult) = await SeedAsync();
        var created = (await Store(connection).CreateAsync(adult, "t", TokenPresets.Direct, 30, TestContext.Current.CancellationToken)).Ok();

        var credential = await Store(connection).FindByPrefixAsync(created.Token.Prefix, TestContext.Current.CancellationToken);

        Assert.Equal((created.Token.Id, adult), (credential!.Id, credential.Person));
        Assert.Null(await Store(connection).FindByPrefixAsync("zzzzzzzz", TestContext.Current.CancellationToken));
    }
}

using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using Duration = NodaTime.Duration;

namespace Nid.Core.Tests;

/// <summary>Issue 517: the writes of a local sign-in, run as Core commands.</summary>
public sealed class LocalSignInUseCasesTests
{
    private static readonly PersonId Alice = PersonId.New();
    private static readonly PersonId Bob = PersonId.New();
    private static readonly Instant Now = Instant.FromUtc(2026, 3, 1, 10, 0);
    private static readonly FixedClock Clock = new(Now);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IActorAccessor As(Actor? actor) => new ActorAccessor { Current = actor };

    private static PersonInfo Info(PersonId id) => new(id, PersonKind.Adult, "Alice", null, 1);

    [Fact]
    public async Task A_failure_counts_with_the_host_s_threshold_and_a_one_minute_base()
    {
        var store = new FakeAccounts { Outcome = new FailureOutcome(true, 2, Now.Plus(Duration.FromMinutes(2))) };
        var command = new RecordLoginFailureCommand(Alice, 5, Duration.FromMinutes(60));

        var outcome = (await new RecordLoginFailureHandler(store, As(Actor.System), Clock).HandleAsync(command, Ct)).Ok();

        Assert.Equal((Alice, Now, 5, Duration.FromMinutes(1), Duration.FromMinutes(60)), store.Failure);
        var locked = Assert.IsType<SecurityEvent>(command.EventFor(outcome));
        Assert.Equal((SecurityEventNames.LoginLocked, (PersonId?)Alice), (locked.Name, locked.Subject));
        Assert.Equal("2", locked.Data["level"]);
        Assert.Equal("2026-03-01T10:02:00Z", locked.Data["until"]);
        Assert.Null(command.EventFor(new FailureOutcome(false, 0, null)));
    }

    [Fact]
    public async Task An_adult_in_session_counts_only_against_their_own_account()
    {
        var store = new FakeAccounts();
        var command = new RecordLoginFailureCommand(Alice, 5, Duration.FromMinutes(60));

        (await new RecordLoginFailureHandler(store, As(Actor.Browser(Alice)), Clock).HandleAsync(command, Ct)).Ok();
        Assert.IsType<UnauthorizedFailure>((await new RecordLoginFailureHandler(store, As(Actor.Browser(Bob)), Clock).HandleAsync(command, Ct)).Fail());
        Assert.IsType<UnauthorizedFailure>((await new RecordLoginFailureHandler(store, As(null), Clock).HandleAsync(command, Ct)).Fail());
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task A_success_clears_the_failures_and_keeps_the_rehash()
    {
        var store = new FakeAccounts();

        Assert.Equal(Alice, (await new RecordLoginSuccessHandler(store, As(Actor.System), Clock).HandleAsync(new RecordLoginSuccessCommand(Alice, "new-hash"), Ct)).Ok());

        Assert.Equal((Alice, Now, (string?)"new-hash"), store.Success);
        Assert.IsType<UnauthorizedFailure>((await new RecordLoginSuccessHandler(store, As(Actor.Browser(Bob)), Clock)
            .HandleAsync(new RecordLoginSuccessCommand(Alice, null), Ct)).Fail());
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task A_rehash_replaces_the_hash_only()
    {
        var store = new FakeAccounts();

        (await new RehashPasswordHandler(store, As(Actor.System)).HandleAsync(new RehashPasswordCommand(Alice, "new-hash"), Ct)).Ok();

        Assert.Equal((Alice, "new-hash"), store.Rehash);
        Assert.Null(store.Success);
        Assert.IsType<UnauthorizedFailure>((await new RehashPasswordHandler(store, As(Actor.Browser(Bob))).HandleAsync(new RehashPasswordCommand(Alice, "x"), Ct)).Fail());
    }

    [Fact]
    public async Task A_recovery_code_passes_once_clears_the_failures_and_is_an_event()
    {
        var store = new FakeAccounts { RecoveryCodes = { ["code-hash"] = 7 } };
        var command = new CompleteTwoStepCommand(Alice, null, "code-hash");

        var passed = (await new CompleteTwoStepHandler(store, As(Actor.System), Clock).HandleAsync(command, Ct)).Ok();

        Assert.Equal(new TwoStepPassed(Alice, 7), passed);
        Assert.Equal((Alice, Now, (string?)null), store.Success);
        var used = Assert.IsType<SecurityEvent>(command.EventFor(passed));
        Assert.Equal((SecurityEventNames.RecoveryCodeUsed, "7"), (used.Name, used.Data["left"]));

        Assert.Equal(LocalAccountCodes.InvalidCode, (await new CompleteTwoStepHandler(store, As(Actor.System), Clock).HandleAsync(command, Ct)).Fail().Code);
    }

    [Fact]
    public async Task A_totp_step_passes_once_and_a_replay_or_no_code_is_refused()
    {
        var store = new FakeAccounts();
        var handler = new CompleteTwoStepHandler(store, As(Actor.System), Clock);
        var command = new CompleteTwoStepCommand(Alice, 42, null);

        var passed = (await handler.HandleAsync(command, Ct)).Ok();
        Assert.Null(passed.RecoveryCodesLeft);
        Assert.Null(command.EventFor(passed));

        store.Success = null;
        var replay = (await handler.HandleAsync(command, Ct)).Fail();
        Assert.Equal(LocalAccountCodes.InvalidCode, replay.Code);
        Assert.Equal(LocalAccountCodes.InvalidCode, (await handler.HandleAsync(new CompleteTwoStepCommand(Alice, null, null), Ct)).Fail().Code);
        Assert.Null(store.Success);
        Assert.IsType<UnauthorizedFailure>((await new CompleteTwoStepHandler(store, As(Actor.Browser(Bob)), Clock).HandleAsync(command, Ct)).Fail());
    }

    [Fact]
    public async Task Turning_the_second_factor_off_asks_a_code_that_passes()
    {
        var store = new FakeAccounts { RecoveryCodes = { ["code-hash"] = 3 } };
        var handler = new DisableMyTotpHandler(store, As(Actor.Browser(Alice)), Clock);

        Assert.Equal(LocalAccountCodes.InvalidCode, (await handler.HandleAsync(new DisableMyTotpCommand(null, "wrong"), Ct)).Fail().Code);
        Assert.Null(store.Disabled);
        Assert.Equal(Alice, (await handler.HandleAsync(new DisableMyTotpCommand(null, "code-hash"), Ct)).Ok().Id);
        Assert.Equal(Alice, store.Disabled);
        Assert.IsType<NotFoundFailure>((await new DisableMyTotpHandler(store, As(Actor.System), Clock).HandleAsync(new DisableMyTotpCommand(1, null), Ct)).Fail());
    }

    [Fact]
    public async Task The_setup_and_a_link_are_stored_and_told_by_their_event()
    {
        var store = new FakeAccounts();

        var admin = (await new CompleteSetupHandler(store).HandleAsync(new CompleteSetupCommand("Alice", "alice", "hash"), Ct)).Ok();
        Assert.Equal(("Alice", "alice", "hash"), store.Setup);
        Assert.Equal(SecurityEventNames.SetupCompleted, new CompleteSetupCommand("Alice", "alice", "hash").EventFor(admin).Name);

        var link = new CompletePasswordLinkCommand("nid_link_x", "alice", "hash");
        var enrolled = (await new CompletePasswordLinkHandler(store, Clock).HandleAsync(link, Ct)).Ok();
        Assert.Equal(("nid_link_x", (string?)"alice", "hash", Now), store.Link);
        var enrolledEvent = link.EventFor(enrolled);
        Assert.Equal((SecurityEventNames.AccountEnrolled, enrolled.LinkId.ToString()), (enrolledEvent.Name, enrolledEvent.Data["link_id"]));
        Assert.Equal(SecurityEventNames.PasswordReset, link.EventFor(enrolled with { Purpose = CredentialLinkPurposes.Reset }).Name);
    }

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    /// <summary>The store's side of a sign-in, recorded; what a sign-in never calls throws.</summary>
    private sealed class FakeAccounts : ILocalAccountStore
    {
        public FailureOutcome Outcome { get; init; } = new(false, 0, null);
        public Dictionary<string, int> RecoveryCodes { get; } = [];
        public (PersonId, Instant, int, Duration, Duration)? Failure { get; private set; }
        public (PersonId, Instant, string?)? Success { get; set; }
        public (PersonId, string)? Rehash { get; private set; }
        public (string, string, string)? Setup { get; private set; }
        public (string, string?, string, Instant)? Link { get; private set; }
        public PersonId? Disabled { get; private set; }
        public int Calls { get; private set; }
        private long lastStep;

        public Task<FailureOutcome> RecordFailureAsync(PersonId person, Instant now, int threshold, Duration lockBase, Duration max, CancellationToken ct)
        {
            Calls++;
            Failure = (person, now, threshold, lockBase, max);
            return Task.FromResult(Outcome);
        }

        public Task RecordSuccessAsync(PersonId person, Instant now, string? rehash, CancellationToken ct)
        {
            Calls++;
            Success = (person, now, rehash);
            return Task.CompletedTask;
        }

        public Task RehashAsync(PersonId person, string passwordHash, CancellationToken ct)
        {
            Rehash = (person, passwordHash);
            return Task.CompletedTask;
        }

        public Task<bool> AcceptTotpStepAsync(PersonId person, long step, CancellationToken ct)
        {
            var accepted = step > lastStep;
            lastStep = Math.Max(lastStep, step);
            return Task.FromResult(accepted);
        }

        public Task<int?> UseRecoveryCodeAsync(PersonId person, string codeHash, Instant now, CancellationToken ct)
            => Task.FromResult(RecoveryCodes.Remove(codeHash, out var left) ? left : (int?)null);

        public Task<Result<PersonInfo>> DisableTotpAsync(PersonId person, bool endSessions, CancellationToken ct)
        {
            Disabled = person;
            return Task.FromResult(Result.Success(Info(person)));
        }

        public Task<Result<PersonInfo>> CreateFirstAdminAsync(string displayName, string login, string passwordHash, CancellationToken ct)
        {
            Setup = (displayName, login, passwordHash);
            return Task.FromResult(Result.Success(Info(Alice)));
        }

        public Task<Result<CompletedLink>> CompleteLinkAsync(string token, string? login, string passwordHash, Instant now, CancellationToken ct)
        {
            Link = (token, login, passwordHash, now);
            return Task.FromResult(Result.Success(new CompletedLink(Info(Alice), CredentialLinkPurposes.Enroll, Guid.CreateVersion7())));
        }

        public Task<LocalCredential?> FindByLoginAsync(string login, CancellationToken ct) => throw new NotSupportedException();
        public Task<LocalCredential?> FindAsync(PersonId person, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> UnlockAsync(PersonId person, CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<NewCredentialLink>> CreateLinkAsync(PersonId person, PersonId? createdBy, string via, Duration lifetime, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> RevokeLinkAsync(PersonId person, CancellationToken ct) => throw new NotSupportedException();
        public Task<CredentialLinkInfo?> FindLinkAsync(string token, Instant now, CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<PersonInfo>> ChangePasswordAsync(PersonId person, string passwordHash, Instant now, CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<PersonInfo>> CreateAdminAdultAsync(string displayName, string? email, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<LocalAccountState>> StatesAsync(Instant now, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> BeginTotpAsync(PersonId person, string protectedSecret, Instant now, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ConfirmTotpAsync(PersonId person, long step, IReadOnlyCollection<string> recoveryCodeHashes, Instant now, Instant pendingSince, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ReplaceRecoveryCodesAsync(PersonId person, IReadOnlyCollection<string> codeHashes, Instant now, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> RecoveryCodesLeftAsync(PersonId person, CancellationToken ct) => throw new NotSupportedException();
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Core.IntegrationTests;

[Collection("postgres")]
public class ProposalStoreTests(PostgresFixture postgres)
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 16, 9, 0);

    private sealed class StepClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private async Task<(string Connection, PersonId Adult, ItemId Item)> SeedAsync()
    {
        var connection = await postgres.NewConnectionStringAsync();
        await using var db = PostgresFixture.NewCoreContext(connection);
        var person = Guid.CreateVersion7();
        var item = Guid.CreateVersion7();
        db.Persons.Add(new PersonRow { Id = person, Kind = "adult", OidcSubject = "sub", DisplayName = "A", CreatedAt = Now });
        db.Items.Add(new ItemRow { Id = item, Module = "investments", Kind = "envelope", DisplayName = "PEA", Currency = "EUR", CreatedAt = Now });
        await db.SaveChangesAsync();
        return (connection, new PersonId(person), new ItemId(item));
    }

    private static ProposalStore Store(string connection) => new(PostgresFixture.NewCoreContext(connection), new StepClock(Now));

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static NewProposal Valuation(PersonId person, ItemId item, string asOf, string amount) => new(
        "investments", "investments.record_valuation", item, person, asOf,
        Json($$$"""{"asOf":"{{{asOf}}}","amount":{"amount":"{{{amount}}}","currency":"EUR"}}"""), 1, [], "api", null, "Relevé");

    // Review Focus 4.
    [Fact]
    public async Task The_same_payload_twice_is_one_proposal()
    {
        var (connection, adult, item) = await SeedAsync();

        var first = await Store(connection).SubmitAsync(Valuation(adult, item, "2025-09-30", "1000.00"), TestContext.Current.CancellationToken);
        var second = await Store(connection).SubmitAsync(Valuation(adult, item, "2025-09-30", "1000.00"), TestContext.Current.CancellationToken);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Proposal.Id, second.Proposal.Id);
        await using var db = PostgresFixture.NewCoreContext(connection);
        Assert.Equal(1, await db.Proposals.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Key_order_and_spacing_do_not_change_the_fingerprint()
    {
        var (connection, adult, item) = await SeedAsync();
        var a = Valuation(adult, item, "2025-09-30", "1000.00");
        var b = a with { Payload = Json("""{ "amount" : { "currency":"EUR", "amount":"1000.00" }, "asOf":"2025-09-30" }""") };

        var first = await Store(connection).SubmitAsync(a, TestContext.Current.CancellationToken);
        var second = await Store(connection).SubmitAsync(b, TestContext.Current.CancellationToken);

        Assert.Equal(first.Proposal.Id, second.Proposal.Id);
    }

    [Fact]
    public async Task Another_amount_on_the_same_date_supersedes_the_pending_one()
    {
        var (connection, adult, item) = await SeedAsync();

        var first = await Store(connection).SubmitAsync(Valuation(adult, item, "2025-09-30", "1000.00"), TestContext.Current.CancellationToken);
        var second = await Store(connection).SubmitAsync(Valuation(adult, item, "2025-09-30", "1100.00"), TestContext.Current.CancellationToken);

        Assert.True(second.Created);
        var old = await Store(connection).FindAsync(first.Proposal.Id, TestContext.Current.CancellationToken);
        Assert.Equal((ProposalStatus.Superseded, second.Proposal.Id), (old!.Status, old.SupersededById!.Value));
        var other = await Store(connection).SubmitAsync(Valuation(adult, item, "2025-10-31", "1200.00"), TestContext.Current.CancellationToken);
        Assert.Equal(ProposalStatus.Pending, (await Store(connection).FindAsync(other.Proposal.Id, TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task Deciding_checks_the_version_and_the_status()
    {
        var (connection, adult, item) = await SeedAsync();
        var p = (await Store(connection).SubmitAsync(Valuation(adult, item, "2025-09-30", "1000.00"), TestContext.Current.CancellationToken)).Proposal;

        Assert.Equal(ProposalCodes.Conflict, (await Store(connection).DecideAsync(p.Id, p.Version + 1, ProposalStatus.Approved, adult, null, null, [], TestContext.Current.CancellationToken)).Fails().Code);

        var applied = Guid.CreateVersion7();
        var decided = (await Store(connection).DecideAsync(p.Id, p.Version, ProposalStatus.Approved, adult, null, null, [applied], TestContext.Current.CancellationToken)).Ok();
        Assert.Equal((ProposalStatus.Approved, adult, applied), (decided.Status, decided.DecidedBy!.Value, decided.AppliedRefs.Single()));

        Assert.Equal(ProposalCodes.Decided, (await Store(connection).DecideAsync(p.Id, decided.Version, ProposalStatus.Rejected, adult, null, null, [], TestContext.Current.CancellationToken)).Fails().Code);
    }

    [Fact]
    public async Task A_warning_is_appended_without_touching_the_rest()
    {
        var (connection, adult, item) = await SeedAsync();
        var p = (await Store(connection).SubmitAsync(Valuation(adult, item, "2025-09-30", "1000.00"), TestContext.Current.CancellationToken)).Proposal;

        await Store(connection).AddWarningAsync(p.Id, new ProposalWarning(ProposalCodes.ApplyFailed, "error", "boom"), TestContext.Current.CancellationToken);

        var after = await Store(connection).FindAsync(p.Id, TestContext.Current.CancellationToken);
        Assert.Equal((ProposalStatus.Pending, ProposalCodes.ApplyFailed), (after!.Status, Assert.Single(after.Warnings).Code));
    }

    [Fact]
    public async Task The_list_holds_only_the_given_targets_and_status_newest_first()
    {
        var (connection, adult, item) = await SeedAsync();
        await Store(connection).SubmitAsync(Valuation(adult, item, "2025-08-31", "900.00"), TestContext.Current.CancellationToken);
        var newer = await Store(connection).SubmitAsync(Valuation(adult, item, "2025-09-30", "1000.00"), TestContext.Current.CancellationToken);

        var listed = await Store(connection).ListAsync(new HashSet<ItemId> { item }, adult, ProposalStatus.Pending, TestContext.Current.CancellationToken);
        Assert.Equal(newer.Proposal.Id, listed[0].Id);
        Assert.Equal(2, listed.Count);
        Assert.Empty(await Store(connection).ListAsync(new HashSet<ItemId>(), adult, ProposalStatus.Pending, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_proposal_without_a_target_is_listed_for_its_submitter_only_and_deduplicated()
    {
        var (connection, adult, _) = await SeedAsync();
        var creation = Valuation(adult, default, "2025-09-30", "1000.00") with { Kind = "realestate.create_property", Target = null, SubjectKey = null };

        var first = await Store(connection).SubmitAsync(creation, TestContext.Current.CancellationToken);
        Assert.False((await Store(connection).SubmitAsync(creation, TestContext.Current.CancellationToken)).Created);

        Assert.Null(first.Proposal.Target);
        Assert.Equal(first.Proposal.Id, Assert.Single(await Store(connection).ListAsync(new HashSet<ItemId>(), adult, ProposalStatus.Pending, TestContext.Current.CancellationToken)).Id);
        Assert.Empty(await Store(connection).ListAsync(new HashSet<ItemId>(), new PersonId(Guid.CreateVersion7()), ProposalStatus.Pending, TestContext.Current.CancellationToken));
    }
}

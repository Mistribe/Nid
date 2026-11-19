using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Banking.IntegrationTests;

/// <summary>#85 decision 4: the dashboard's everyday spending, the month's spending less linked lines and tax. Today is 2025-10-15.</summary>
public class DashboardSpendingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LocalDate Today = new(2025, 10, 15);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<SpendingPart> EverydayAsync(NidApp app, PersonId person, VisibilityScope scope, Actor? actor = null)
    {
        await using var services = app.Services.CreateAsyncScope();
        services.ServiceProvider.GetRequiredService<IActorAccessor>().Current = actor ?? Actor.Browser(person);
        var read = await services.ServiceProvider.GetRequiredService<ISpendingContribution>().EverydayAsync(person, scope, Today, Ct);
        Assert.True(read.TryGetValue(out var part));
        return part!;
    }

    private static Dictionary<string, decimal> Slices(SpendingPart part)
        => part.ByCategory.ToDictionary(s => s.Name, s => Assert.Single(s.Amount).Value);

    private static async Task LinkAsync(NidApp app, Guid line)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<ILinkStore>().SetAsync(
            new BankLink(line, "subscriptions", new ItemId(Guid.NewGuid()), "charge", null, LinkOutcome.AsExpected, null, "job", Instant.FromUtc(2025, 10, 15, 12, 0)),
            TransactionLinkState.Linked, Ct);
    }

    [Fact]
    public async Task Linked_lines_and_tax_are_left_out_uncategorised_debits_count_and_the_scope_is_the_dashboard_s()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Commun"), BankingApi.Account("uid-2", "hash-2", null, "Perso A"));
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-b", "hash-b", null, "Perso B"));
        var a = await SpendingTests.AccountsAsync(app, app.AdultA);
        var b = await SpendingTests.AccountsAsync(app, app.AdultB);
        await SpendingTests.PatchAsync(app, app.AdultA, a["Commun"],
            new { envelopeId = (await app.NewEnvelopeAsync("Commun", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000))).Value });
        await SpendingTests.PatchAsync(app, app.AdultA, a["Perso A"], new { envelopeId = (await app.NewEnvelopeAsync("Perso A", app.AdultA, Visibility.Private)).Value });
        await SpendingTests.PatchAsync(app, app.AdultB, b["Perso B"], new { envelopeId = (await app.NewEnvelopeAsync("Perso B", app.AdultB, Visibility.Private)).Value });

        var joint = a["Commun"];
        await SpendingTests.LineAsync(app, joint, new(2025, 10, 3), -80m, "food.groceries");
        await SpendingTests.LineAsync(app, joint, new(2025, 10, 4), -20m, "food.restaurants");
        // The streaming line linked to its subscription is already in the margin.
        await LinkAsync(app, await SpendingTests.LineAsync(app, joint, new(2025, 10, 5), -12.99m, "leisure.subscriptions"));
        // So is the tax: its payment is never linked, its category leaves it out.
        await SpendingTests.LineAsync(app, joint, new(2025, 10, 6), -300m, "taxes");
        await TaxChildLineAsync(app, joint, -40m);
        await SpendingTests.LineAsync(app, joint, new(2025, 10, 7), -25m);
        await SpendingTests.LineAsync(app, joint, new(2025, 10, 8), 15m); // an uncategorised credit never counts
        await SpendingTests.LineAsync(app, joint, new(2025, 9, 20), -45m, "food.groceries"); // last month
        await SpendingTests.LineAsync(app, a["Perso A"], new(2025, 10, 4), -10m, "transport.fuel");
        await SpendingTests.LineAsync(app, b["Perso B"], new(2025, 10, 4), -1000m, "food.groceries");

        var household = await EverydayAsync(app, app.AdultA, VisibilityScope.Household);
        Assert.Equal(125m, Assert.Single(household.Total).Value);
        Assert.Equal(new Dictionary<string, decimal> { ["Alimentation"] = 100m, ["Non classées"] = 25m }, Slices(household));
        // « Non classées » is told by its missing category, never by its name.
        Assert.Equal([(true, "Alimentation"), (false, "Non classées")],
            household.ByCategory.Select(s => (s.Category is not null, s.Name)).OrderBy(s => s.Name, StringComparer.Ordinal));

        // « Moi » adds the reader's own account, never the other adult's.
        var mine = await EverydayAsync(app, app.AdultA, VisibilityScope.Mine);
        Assert.Equal(135m, Assert.Single(mine.Total).Value);
        Assert.Equal(10m, Slices(mine)["Transport"]);
        Assert.Equal(1125m, Assert.Single((await EverydayAsync(app, app.AdultB, VisibilityScope.Mine)).Total).Value);

        // Read for Bob as Alice, or by the system: the accounts would be the actor's, so nothing.
        Assert.Same(SpendingPart.None, await EverydayAsync(app, app.AdultB, VisibilityScope.Mine, Actor.Browser(app.AdultA)));
        Assert.Same(SpendingPart.None, await EverydayAsync(app, app.AdultA, VisibilityScope.Household, Actor.System));
    }

    /// <summary>A line filed in a household's own child of « Impôts & taxes ».</summary>
    private static async Task TaxChildLineAsync(NidApp app, Guid account, decimal amount)
    {
        var line = await SpendingTests.LineAsync(app, account, new(2025, 10, 9), amount);
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var categories = scope.ServiceProvider.GetRequiredService<ICategoryStore>();
        Assert.True((await categories.CreateAsync(Nid.Banking.Domain.CategorySeed.Find("taxes")!.Id, "Taxe locale", CategoryKind.Expense, Ct)).TryGetValue(out var child));
        Assert.Equal(1, await categories.SetForLinesAsync([line], child!.Id, CategorySource.Hand, "person:test", Instant.FromUtc(2025, 10, 15, 12, 0), true, Ct));
    }

    [Fact]
    public async Task An_account_that_spent_nothing_reads_zero_and_no_account_or_banking_off_reads_nothing()
    {
        await using (var app = await BankingApi.StartAsync(fixture))
        {
            Assert.Same(SpendingPart.None, await EverydayAsync(app, app.AdultA, VisibilityScope.Household));
            await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Perso A"));
            var part = await EverydayAsync(app, app.AdultA, VisibilityScope.Mine);
            Assert.Equal((Currency.Eur, 0m), (part.Total.Single().Key, part.Total.Single().Value));
            Assert.Empty(part.ByCategory);
        }

        await using var off = await BankingApi.StartAsync(fixture, enable: false);
        Assert.Same(SpendingPart.None, await EverydayAsync(off, off.AdultA, VisibilityScope.Mine));
    }
}

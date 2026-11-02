using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Core.Infrastructure.Persistence;
using Nid.Income.Infrastructure.Persistence;
using Nid.Investments.Infrastructure.Persistence;
using Nid.Platform.Infrastructure.Audit;
using Nid.Platform.Infrastructure.Persistence;
using Nid.RealEstate.Infrastructure.Persistence;
using Nid.Subscriptions.Infrastructure.Persistence;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Audit;

/// <summary>1F3a spec §9 test 3: no write path is left out of the log without a decision recorded here.</summary>
public partial class AuditCompletenessTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Every_mapped_entity_is_audited_or_excluded()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var maps = scope.ServiceProvider.GetServices<IAuditMap>().ToList();
        var known = maps.SelectMany(m => m.Audited.Keys.Concat(m.Excluded)).ToHashSet();

        DbContext[] contexts =
        [
            scope.ServiceProvider.GetRequiredService<CoreDbContext>(),
            scope.ServiceProvider.GetRequiredService<SubscriptionsDbContext>(),
            scope.ServiceProvider.GetRequiredService<InvestmentsDbContext>(),
            scope.ServiceProvider.GetRequiredService<IncomeDbContext>(),
            scope.ServiceProvider.GetRequiredService<RealEstateDbContext>(),
            scope.ServiceProvider.GetRequiredService<BankingDbContext>(),
            scope.ServiceProvider.GetRequiredService<PlatformDbContext>(),
        ];
        var unknown = contexts.SelectMany(c => c.Model.GetEntityTypes()).Select(t => t.ClrType).Distinct()
            .Where(t => !known.Contains(t)).ToList();

        Assert.True(unknown.Count == 0, "Neither audited nor excluded (spec §4.4):\n  " + string.Join("\n  ", unknown.Select(t => t.FullName)));
    }

    [Fact]
    public void No_module_context_exposes_the_log()
    {
        foreach (var context in new[] { typeof(CoreDbContext), typeof(SubscriptionsDbContext), typeof(InvestmentsDbContext), typeof(IncomeDbContext), typeof(RealEstateDbContext), typeof(BankingDbContext) })
            Assert.DoesNotContain(context.GetProperties(), p => p.PropertyType == typeof(DbSet<AuditEntryRow>));
    }

    /// <summary>
    /// Spec U17: a bulk statement never reaches the interceptor. These are the ones that exist, each on an excluded
    /// table, a lock, or an audited column covered otherwise. A new one fails here until it is decided.
    /// </summary>
    private static readonly Dictionary<string, int> AllowedBulkStatements = new()
    {
        ["src/Nid.Banking.Infrastructure/Persistence/AccountStore.cs"] = 1,  // last_valued_on, balance_conflict_on: the sync's bookkeeping, not a decision (as last_used_at)
        ["src/Nid.Banking.Infrastructure/Persistence/ConnectionStore.cs"] = 1, // a person's file-import advisory lock: no row written
        ["src/Nid.Banking.Infrastructure/Persistence/RuleStore.cs"] = 2,     // last_used_at (not audited); recurring_group: excluded (the job's memory)
        ["src/Nid.Banking.Infrastructure/Persistence/SyncRunLog.cs"] = 3,    // sync_run: excluded
        ["src/Nid.Banking.Infrastructure/Persistence/TransactionStore.cs"] = 3, // transaction: excluded; the account's advisory lock; reconciled_at (the job's mark, données brutes de la banque, ni décision ni réglage)
        ["src/Nid.Core.Infrastructure/Persistence/FxRateStore.cs"] = 1,       // fx_rate: excluded (U8)
        ["src/Nid.Core.Infrastructure/Persistence/FxRunLog.cs"] = 3,          // fx_sync_run: excluded
        ["src/Nid.Core.Infrastructure/Persistence/HouseholdSettings.cs"] = 1, // advisory lock
        ["src/Nid.Core.Infrastructure/Persistence/PersonalAccessTokenStore.cs"] = 3, // lock; revoked_at (token_revoked) and last_used_at (not audited)
        ["src/Nid.Core.Infrastructure/Persistence/ProposalStore.cs"] = 3, // the target's (or, without one, the submitter's) row lock; warnings append (not a decision, not in the write log)
        ["src/Nid.Core.Infrastructure/Persistence/PersonRegistry.cs"] = 3,    // advisory lock; the person's row lock for preferences (writes nothing); session_epoch (session_revoked)
        ["src/Nid.Subscriptions.Infrastructure/Persistence/CatalogRunLog.cs"] = 3, // catalog_sync_run: excluded
        ["src/Nid.Subscriptions.Infrastructure/Persistence/ProviderStore.cs"] = 3, // lock; archived_at and icons (catalog run)
        ["src/Nid.Investments.Infrastructure/Persistence/PriceRunLog.cs"] = 3, // price_run: excluded
        ["src/Nid.Investments.Infrastructure/Persistence/SecurityPriceStore.cs"] = 3, // security_price: excluded
        ["src/Nid.Platform.Infrastructure/Notifications/NotificationStore.cs"] = 8, // notification, notification_run: excluded (+1: the ExecuteUpdateAsync of ResolveKeyAsync)
        ["src/Nid.Platform.Infrastructure/Admin/JobRunStore.cs"] = 3, // job_run: excluded (a technical journal)
        ["src/Nid.Platform.Infrastructure/Notifications/DeliveryStore.cs"] = 7, // notification_delivery, notification_digest, notification: excluded (the outbox)
        ["src/Nid.Platform.Infrastructure/Persistence/ImportStagingStore.cs"] = 4, // import staging: excluded
        ["src/Nid.RealEstate.Infrastructure/Persistence/IrlRunLog.cs"] = 3, // irl_run: excluded
        ["src/Nid.RealEstate.Infrastructure/Persistence/IrlStore.cs"] = 1, // irl: excluded (reference data, like fx_rate)
        ["src/Nid.RealEstate.Infrastructure/Persistence/LoanStore.cs"] = 1, // the loan's row lock (3A2 precision 5): writes nothing
        ["src/Nid.RealEstate.Infrastructure/Persistence/PropertyStore.cs"] = 1, // the property's row lock (3C1 precision 4): writes nothing
    };

    [GeneratedRegex(@"\.Execute(SqlRaw|SqlInterpolated|Sql|Update|Delete)Async\(")]
    private static partial Regex BulkStatement();

    [Fact]
    public void Bulk_statements_appear_only_where_decided()
    {
        var root = RepositoryRoot();
        var found = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => (File: Path.GetRelativePath(root, f).Replace('\\', '/'), Count: BulkStatement().Matches(File.ReadAllText(f)).Count))
            .Where(x => x.Count > 0)
            .ToDictionary(x => x.File, x => x.Count);

        Assert.Equal(AllowedBulkStatements.OrderBy(p => p.Key), found.OrderBy(p => p.Key));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Nid.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Nid.slnx not found above the test binaries.");
    }
}

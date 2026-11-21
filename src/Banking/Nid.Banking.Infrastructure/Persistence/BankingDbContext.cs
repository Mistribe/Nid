using Nid.Platform.Infrastructure.Persistence;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nid.Platform.Infrastructure.Audit;

namespace Nid.Banking.Infrastructure.Persistence;

public sealed class BankingDbContext(DbContextOptions<BankingDbContext> options) : DbContext(options)
{
    public const string Schema = "banking";

    public DbSet<ApplicationRow> Applications => Set<ApplicationRow>();

    public DbSet<ConnectionRow> Connections => Set<ConnectionRow>();

    public DbSet<AccountRow> Accounts => Set<AccountRow>();

    public DbSet<AccountSourceRow> Sources => Set<AccountSourceRow>();

    public DbSet<BalanceRow> Balances => Set<BalanceRow>();

    public DbSet<TransactionRow> Transactions => Set<TransactionRow>();

    public DbSet<TransactionPartRow> TransactionParts => Set<TransactionPartRow>();

    public DbSet<LinkRow> Links => Set<LinkRow>();

    public DbSet<CategoryRow> Categories => Set<CategoryRow>();

    public DbSet<CategoryRuleRow> CategoryRules => Set<CategoryRuleRow>();

    public DbSet<CategoryBudgetRow> CategoryBudgets => Set<CategoryBudgetRow>();

    public DbSet<SuggestionDismissalRow> SuggestionDismissals => Set<SuggestionDismissalRow>();

    public DbSet<RuleRow> Rules => Set<RuleRow>();

    public DbSet<RecurringGroupRow> RecurringGroups => Set<RecurringGroupRow>();

    public DbSet<SyncRunRow> SyncRuns => Set<SyncRunRow>();
    public DbSet<AccountReadRow> AccountReads => Set<AccountReadRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        // #84: the operations search ignores accents.
        modelBuilder.HasPostgresExtension("unaccent");

        modelBuilder.Entity<ApplicationRow>(e =>
        {
            e.ToTable("application");
            // The person lives in core: no foreign key across contexts.
            e.HasKey(x => x.PersonId);
            e.Property(x => x.PersonId).ValueGeneratedNever();
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<ConnectionRow>(e =>
        {
            e.ToTable("connection", t =>
            {
                t.HasCheckConstraint("ck_connection_status", "status in ('pending','active','expired','revoked','error')");
                t.HasCheckConstraint("ck_connection_source", "source in ('enable_banking','file')");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            // The person lives in core: no foreign key across contexts.
            e.HasIndex(x => x.PersonId);
            // The accounts a person imports from files hang off one connection of theirs.
            e.HasIndex(x => new { x.PersonId, x.Source }).IsUnique().HasFilter("source = 'file'");
            e.HasIndex(x => x.StateHash).IsUnique();
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<AccountRow>(e =>
        {
            e.ToTable("account", t =>
            {
                t.HasCheckConstraint("ck_account_kind", "kind in ('checking','savings','card','other')");
                t.HasCheckConstraint("ck_account_iban_masked", "iban_masked is null or char_length(iban_masked) <= 4");
                t.HasCheckConstraint("ck_account_household_name", "household_name is null or char_length(household_name) between 1 and 100");
                t.HasCheckConstraint("ck_account_card_debit", "card_debit is null or card_debit in ('immediate','deferred')");
                t.HasCheckConstraint("ck_account_holder", "holder is null or char_length(holder) between 1 and 200");
                t.HasCheckConstraint("ck_account_product", "product is null or char_length(product) between 1 and 200");
                t.HasCheckConstraint("ck_account_card_min_payments", "card_min_payments is null or (kind = 'card' and card_min_payments between 1 and 31)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Currency).HasColumnType("char(3)");
            e.HasIndex(x => x.IbanHash).IsUnique();
            // One account per envelope (spec §2.3); the envelope lives in investments, so no foreign key.
            e.HasIndex(x => x.EnvelopeItemId).IsUnique();
            e.HasOne<AccountRow>().WithMany().HasForeignKey(x => x.CardOfAccountId);
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<AccountSourceRow>(e =>
        {
            e.ToTable("account_source");
            e.HasKey(x => new { x.AccountId, x.ConnectionId });
            e.HasIndex(x => x.ConnectionId);
            e.HasIndex(x => x.IdentificationHash);
            e.HasOne<AccountRow>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ConnectionRow>().WithMany().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BalanceRow>(e =>
        {
            e.ToTable("balance");
            e.HasKey(x => new { x.AccountId, x.AsOf });
            e.Property(x => x.Amount).HasColumnType("numeric(19,4)");
            e.HasOne<AccountRow>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TransactionRow>(e =>
        {
            e.ToTable("transaction", t =>
            {
                t.HasCheckConstraint("ck_transaction_link_state", "link_state in ('unlinked','linked','ignored','internal')");
                t.HasCheckConstraint("ck_transaction_fetched_via", "fetched_via in ('api','file')");
                t.HasCheckConstraint("ck_transaction_category_source", "category_source is null or category_source in ('link','rule','hand','assistant','hint','split')");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Amount).HasColumnType("numeric(19,4)");
            e.Property(x => x.Currency).HasColumnType("char(3)");
            e.Property(x => x.BankCode).HasMaxLength(140);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => new { x.AccountId, x.ProviderRef }).IsUnique();
            e.HasIndex(x => new { x.AccountId, x.Fingerprint }).IsUnique();
            e.HasIndex(x => new { x.AccountId, x.BookedOn });
            e.HasIndex(x => new { x.AccountId, x.CategoryId, x.BookedOn });
            e.Property(x => x.SuggestedPattern).HasMaxLength(60);
            e.HasIndex(x => new { x.AccountId, x.SuggestedPattern });
            e.HasOne<AccountRow>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CategoryRow>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CategoryRuleRow>().WithMany().HasForeignKey(x => x.CategoryRuleId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<TransactionPartRow>(e =>
        {
            e.ToTable("transaction_part");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Amount).HasColumnType("numeric(19,4)");
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => new { x.TransactionId, x.Position }).IsUnique();
            e.HasIndex(x => x.CategoryId);
            e.HasOne<TransactionRow>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CategoryRow>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CategoryRow>(e =>
        {
            e.ToTable("category", t =>
            {
                t.HasCheckConstraint("ck_category_kind", "kind in ('expense','income','transfer')");
                t.HasCheckConstraint("ck_category_name", "char_length(name) between 1 and 60");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            // Sibling names are unique whatever the case, checked by the store: a null parent defeats a unique index.
            e.HasIndex(x => x.Key).IsUnique();
            e.HasOne<CategoryRow>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CategoryRow>().WithMany().HasForeignKey(x => x.MergedIntoId).OnDelete(DeleteBehavior.Restrict);
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<CategoryRuleRow>(e =>
        {
            e.ToTable("category_rule", t =>
            {
                t.HasCheckConstraint("ck_category_rule_pattern", "char_length(pattern) between 2 and 60");
                t.HasCheckConstraint("ck_category_rule_direction", "direction is null or direction in ('debit','credit')");
                t.HasCheckConstraint("ck_category_rule_amounts",
                    "(min_amount is null or min_amount >= 0) and (max_amount is null or max_amount >= 0) and (min_amount is null or max_amount is null or min_amount <= max_amount)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.MinAmount).HasColumnType("numeric(19,4)");
            e.Property(x => x.MaxAmount).HasColumnType("numeric(19,4)");
            // The author lives in core: no foreign key across contexts.
            e.HasOne<AccountRow>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CategoryRow>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<SuggestionDismissalRow>(e =>
        {
            e.ToTable("suggestion_dismissal", t =>
            {
                t.HasCheckConstraint("ck_suggestion_dismissal_pattern", "char_length(pattern) between 2 and 60");
                t.HasCheckConstraint("ck_suggestion_dismissal_direction", "direction in ('debit','credit')");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.Pattern, x.Direction, x.CategoryId }).IsUnique();
            // The author lives in core: no foreign key across contexts.
            e.HasOne<CategoryRow>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CategoryBudgetRow>(e =>
        {
            e.ToTable("category_budget", t =>
            {
                t.HasCheckConstraint("ck_category_budget_amount", "amount is null or amount >= 0");
                t.HasCheckConstraint("ck_category_budget_period", "period in ('month','year')");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Amount).HasColumnType("numeric(19,4)");
            // One per category; one per branch (a parent and its children) is the store's to check.
            e.HasIndex(x => x.CategoryId).IsUnique();
            e.HasOne<CategoryRow>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<LinkRow>(e =>
        {
            e.ToTable("link", t => t.HasCheckConstraint(
                "ck_link_outcome", "outcome in ('as_expected','price_differs','income','contribution','internal_transfer','card_settlement')"));
            e.HasKey(x => x.TransactionId);
            e.Property(x => x.TransactionId).ValueGeneratedNever();
            e.Property(x => x.ExpectedAmount).HasColumnType("numeric(19,4)");
            e.Property(x => x.ExpectedCurrency).HasColumnType("char(3)");
            // The item lives in another module: no foreign key.
            e.HasIndex(x => x.TargetItemId);
            e.HasOne<TransactionRow>().WithOne().HasForeignKey<LinkRow>(x => x.TransactionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RuleRow>(e =>
        {
            e.ToTable("rule");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.AccountId, x.CreatedByPersonId, x.LabelStem }).IsUnique();
            e.HasOne<AccountRow>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecurringGroupRow>(e =>
        {
            e.ToTable("recurring_group");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.AccountId, x.LabelStem }).IsUnique();
            e.HasOne<AccountRow>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SyncRunRow>(e =>
        {
            e.ToTable("sync_run", t =>
            {
                t.HasCheckConstraint("ck_sync_run_trigger", "trigger in ('schedule','manual')");
                t.HasCheckConstraint("ck_sync_run_outcome", "outcome in ('running','succeeded','failed')");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Transactions).HasDefaultValue(0);
            e.Property(x => x.RateLimited).HasDefaultValue(0);
            // At most one running row, as investments.price_run: a second start fails on this index.
            e.HasIndex(x => x.Outcome).IsUnique().HasFilter("outcome = 'running'");
            e.HasIndex(x => x.StartedAt);
        });

        modelBuilder.Entity<AccountReadRow>(e =>
        {
            e.ToTable("account_read");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.AccountId, x.ReadAt });
            e.HasIndex(x => x.ReadAt);
            e.HasOne<AccountRow>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.MapAuditLog(ownedHere: false);
        ModelNaming.Apply(modelBuilder);
    }

    private static void MapXmin(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<uint> property)
        => property.HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
}

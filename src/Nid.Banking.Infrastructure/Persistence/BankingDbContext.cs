using System.Text;
using Microsoft.EntityFrameworkCore;
using Nid.Platform.Infrastructure.Audit;

namespace Nid.Banking.Infrastructure.Persistence;

public sealed class BankingDbContext(DbContextOptions<BankingDbContext> options) : DbContext(options)
{
    public const string Schema = "banking";

    public DbSet<ConnectionRow> Connections => Set<ConnectionRow>();

    public DbSet<AccountRow> Accounts => Set<AccountRow>();

    public DbSet<AccountSourceRow> Sources => Set<AccountSourceRow>();

    public DbSet<BalanceRow> Balances => Set<BalanceRow>();

    public DbSet<TransactionRow> Transactions => Set<TransactionRow>();

    public DbSet<SyncRunRow> SyncRuns => Set<SyncRunRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

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
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Amount).HasColumnType("numeric(19,4)");
            e.Property(x => x.Currency).HasColumnType("char(3)");
            e.HasIndex(x => new { x.AccountId, x.ProviderRef }).IsUnique();
            e.HasIndex(x => new { x.AccountId, x.Fingerprint }).IsUnique();
            e.HasIndex(x => new { x.AccountId, x.BookedOn });
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
            // At most one running row, as investments.price_run: a second start fails on this index.
            e.HasIndex(x => x.Outcome).IsUnique().HasFilter("outcome = 'running'");
            e.HasIndex(x => x.StartedAt);
        });

        modelBuilder.MapAuditLog(ownedHere: false);
        ApplySnakeCase(modelBuilder);
    }

    private static void MapXmin(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<uint> property)
        => property.HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

    private static void ApplySnakeCase(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
        {
            if (property.Name == nameof(ConnectionRow.Version))
                continue; // mapped to the system column xmin

            property.SetColumnName(ToSnakeCase(property.Name));
        }

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()!;
            entity.FindPrimaryKey()?.SetName($"pk_{table}");

            foreach (var index in entity.GetIndexes())
                index.SetDatabaseName($"ix_{table}_{string.Join('_', index.Properties.Select(p => p.GetColumnName()))}");

            foreach (var foreignKey in entity.GetForeignKeys())
                foreignKey.SetConstraintName(
                    $"fk_{table}_{foreignKey.PrincipalEntityType.GetTableName()}_{string.Join('_', foreignKey.Properties.Select(p => p.GetColumnName()))}");
        }
    }

    internal static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Nid.Platform.Infrastructure.Audit;
using Nid.Platform.Infrastructure.Persistence;

namespace Nid.Ai.Infrastructure.Persistence;

/// <summary>The <c>ai</c> schema (<c>docs/ai/README.md</c> §13): the worker registry and Nid's keys towards workers.</summary>
public sealed class AiDbContext(DbContextOptions<AiDbContext> options) : DbContext(options)
{
    public const string Schema = "ai";

    public DbSet<ModelWorkerRow> Workers => Set<ModelWorkerRow>();

    public DbSet<AiKeyRow> Keys => Set<AiKeyRow>();

    public DbSet<JobRow> Jobs => Set<JobRow>();

    public DbSet<AiSettingsRow> Settings => Set<AiSettingsRow>();

    public DbSet<ConsentRow> Consents => Set<ConsentRow>();

    public DbSet<LlmCallRow> LlmCalls => Set<LlmCallRow>();

    public DbSet<LlmCredentialRow> Credentials => Set<LlmCredentialRow>();

    public DbSet<CloudProfileRow> CloudProfiles => Set<CloudProfileRow>();

    public DbSet<BudgetMonthRow> BudgetMonths => Set<BudgetMonthRow>();

    public DbSet<BudgetReservationRow> BudgetReservations => Set<BudgetReservationRow>();

    public DbSet<ConversationRow> Conversations => Set<ConversationRow>();

    public DbSet<ConversationMessageRow> ConversationMessages => Set<ConversationMessageRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ModelWorkerRow>(e =>
        {
            e.ToTable("model_worker", t =>
            {
                t.HasCheckConstraint("ck_model_worker_placement", "placement in ('local', 'remote')");
                t.HasCheckConstraint("ck_model_worker_status", "status in ('pending', 'active', 'disabled', 'revoked')");
                t.HasCheckConstraint("ck_model_worker_port", "port between 1 and 65535");
                t.HasCheckConstraint("ck_model_worker_revoked", "(status = 'revoked') = (revoked_at is not null)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(60);
            e.Property(x => x.WorkerKeyFingerprint).HasColumnType("char(64)");
            e.Property(x => x.TlsCertificateSha256).HasColumnType("char(64)");
            e.Property(x => x.LastDeclaration).HasColumnType("jsonb");
            e.HasIndex(x => new { x.Host, x.Port }).IsUnique().HasFilter("status <> 'revoked'");
            e.HasOne<AiKeyRow>().WithMany().HasForeignKey(x => x.NidKeyId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });

        modelBuilder.Entity<AiKeyRow>(e =>
        {
            e.ToTable("ai_key", t => t.HasCheckConstraint("ck_ai_key_status", "status in ('active', 'retiring')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Fingerprint).HasColumnType("char(64)");
            e.HasIndex(x => x.Status).IsUnique().HasFilter("status = 'active'");
        });

        modelBuilder.Entity<JobRow>(e =>
        {
            e.ToTable("job", t =>
            {
                t.HasCheckConstraint("ck_job_status", "status in ('queued', 'running', 'succeeded', 'failed', 'dead')");
                t.HasCheckConstraint("ck_job_attempts", "attempts >= 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Type).HasMaxLength(64);
            e.Property(x => x.Input).HasColumnType("jsonb");
            e.Property(x => x.InputHash).HasColumnType("char(64)");
            e.Property(x => x.DataClass).HasMaxLength(32);
            e.Property(x => x.Result).HasColumnType("jsonb");
            e.HasIndex(x => new { x.Type, x.InputHash }).IsUnique().HasFilter("status in ('queued', 'running')");
            e.HasIndex(x => new { x.Status, x.RunAfter });
            // The jobs page, newest first (#476).
            e.HasIndex(x => new { x.CreatedAt, x.Id });
            e.HasOne<ModelWorkerRow>().WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });

        modelBuilder.Entity<LlmCallRow>(e =>
        {
            e.ToTable("llm_call", t => t.HasCheckConstraint("ck_llm_call_status",
                "status in ('succeeded', 'refused', 'error', 'no_destination', 'blocked_budget')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Task).HasMaxLength(16);
            e.Property(x => x.Use).HasMaxLength(16);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Code).HasMaxLength(64);
            e.Property(x => x.ProfileId).HasMaxLength(64);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.Tier).HasMaxLength(8);
            e.Property(x => x.ReservedEur).HasPrecision(12, 6);
            e.Property(x => x.CostEstimateEur).HasPrecision(12, 6);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.PersonId, x.CreatedAt });
        });

        modelBuilder.Entity<LlmCredentialRow>(e =>
        {
            e.ToTable("llm_provider_credential");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.Hint).HasMaxLength(8);
            // One active key per provider.
            e.HasIndex(x => x.Provider).IsUnique().HasFilter("revoked_at is null");
        });

        modelBuilder.Entity<BudgetMonthRow>(e =>
        {
            e.ToTable("budget_month", t => t.HasCheckConstraint("ck_budget_month_amounts", "spent >= 0 and reserved >= 0"));
            e.HasKey(x => x.Month);
            e.Property(x => x.Spent).HasPrecision(12, 6);
            e.Property(x => x.Reserved).HasPrecision(12, 6);
        });

        modelBuilder.Entity<BudgetReservationRow>(e =>
        {
            e.ToTable("budget_reservation");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Amount).HasPrecision(12, 6);
            e.HasIndex(x => x.ExpiresAt);
        });

        modelBuilder.Entity<CloudProfileRow>(e =>
        {
            e.ToTable("cloud_profile");
            e.HasKey(x => x.ProfileId);
            e.Property(x => x.ProfileId).HasMaxLength(64);
        });

        modelBuilder.Entity<AiSettingsRow>(e =>
        {
            e.ToTable("ai_settings", t => t.HasCheckConstraint("ck_ai_settings_single", "id = 1"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne<ConsentRow>().WithMany().HasForeignKey(x => x.RemoteConsentId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Uses).HasColumnType("jsonb");
            e.Property(x => x.Models).HasColumnType("jsonb");
            e.Property(x => x.Region).HasMaxLength(16);
            e.Property(x => x.MonthlyBudgetEur).HasPrecision(10, 2);
            e.Property(x => x.ConversationRetentionDays);
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });

        modelBuilder.Entity<ConversationRow>(e =>
        {
            e.ToTable("conversation");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Title).HasMaxLength(Nid.Ai.Application.ConversationRules.MaxTitle);
            e.Property(x => x.PolicyClass).HasMaxLength(80);
            e.Property(x => x.Provider).HasMaxLength(200);
            e.Property(x => x.Model).HasMaxLength(200);
            e.HasIndex(x => new { x.PersonId, x.UpdatedAt });
            e.HasIndex(x => x.UpdatedAt);
        });

        modelBuilder.Entity<ConversationMessageRow>(e =>
        {
            e.ToTable("conversation_message", t => t.HasCheckConstraint("ck_conversation_message_role", "role in ('user', 'assistant')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Role).HasMaxLength(16);
            e.HasOne<ConversationRow>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ConversationId, x.Seq }).IsUnique();
        });

        modelBuilder.Entity<ConsentRow>(e =>
        {
            e.ToTable("consent", t => t.HasCheckConstraint("ck_consent_subject", "subject in ('remote_workers')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Subject).HasMaxLength(32);
            e.HasIndex(x => new { x.Subject, x.At });
        });

        modelBuilder.MapAuditLog(ownedHere: false);
        ModelNaming.Apply(modelBuilder);
    }
}

public static class AiDbContextConfiguration
{
    public static DbContextOptionsBuilder<AiDbContext> Configure(this DbContextOptionsBuilder<AiDbContext> builder, string connectionString)
    {
        builder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", AiDbContext.Schema);
        });
        return builder;
    }
}

/// <summary>Used by <c>dotnet ef</c> only; the connection string is never opened when adding a migration.</summary>
public sealed class AiDbContextFactory : IDesignTimeDbContextFactory<AiDbContext>
{
    public AiDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<AiDbContext>().Configure("Host=localhost;Database=nid_design").Options);
}

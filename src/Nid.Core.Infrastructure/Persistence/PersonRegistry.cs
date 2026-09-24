using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Infrastructure.Persistence;

public sealed class PersonRegistry(CoreDbContext db, IClock clock) : IPersonRegistry
{
    public const int MaxAdults = 2;
    private const long ProvisioningLockKey = 0x4E49_4401; // "NID" + 1

    public async Task<Result<PersonInfo>> ProvisionAdultAsync(
        string oidcSubject, string displayName, string? email, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Two first logins at the same instant must not both pass the cap check.
        await db.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({ProvisioningLockKey})", ct);

        var existing = await db.Persons.SingleOrDefaultAsync(p => p.OidcSubject == oidcSubject, ct);
        if (existing is not null)
        {
            existing.DisplayName = displayName;
            existing.Email = email;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Result.Success(ToInfo(existing));
        }

        var adults = await db.Persons.CountAsync(p => p.Kind == "adult", ct);
        if (adults >= MaxAdults)
            return Result.Failure<PersonInfo>(new Failure(PersonCodes.AdultCap, "This household already has two adults.", null));

        var row = new PersonRow
        {
            Id = PersonId.New().Value,
            Kind = "adult",
            OidcSubject = oidcSubject,
            DisplayName = displayName,
            Email = email,
            CreatedAt = clock.GetCurrentInstant(),
        };
        db.Persons.Add(row);

        if (!await db.HouseholdSettings.AnyAsync(ct))
            db.HouseholdSettings.Add(new HouseholdSettingsRow());

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Success(ToInfo(row));
    }

    public async Task<Result<PersonInfo>> CreateDependentAsync(string displayName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return Result.Failure<PersonInfo>(new Failure(PersonCodes.NameRequired, "A name is required.", null));

        var row = new PersonRow
        {
            Id = PersonId.New().Value,
            Kind = "dependent",
            DisplayName = displayName.Trim(),
            CreatedAt = clock.GetCurrentInstant(),
        };
        db.Persons.Add(row);
        await db.SaveChangesAsync(ct);
        return Result.Success(ToInfo(row));
    }

    public async Task<PersonInfo?> FindAsync(PersonId id, CancellationToken ct)
    {
        var row = await db.Persons.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id.Value, ct);
        return row is null ? null : ToInfo(row);
    }

    public async Task<IReadOnlyList<PersonId>> AdultIdsAsync(CancellationToken ct)
        => await db.Persons.AsNoTracking().Where(p => p.Kind == "adult").Select(p => new PersonId(p.Id)).ToListAsync(ct);

    public async Task<IReadOnlyList<PersonInfo>> ListAsync(CancellationToken ct)
        => (await db.Persons.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync(ct)).Select(ToInfo).ToList();

    public async Task<Preferences?> PreferencesAsync(PersonId id, CancellationToken ct)
    {
        var stored = await db.Persons.AsNoTracking().Where(p => p.Id == id.Value).Select(p => p.Preferences).SingleOrDefaultAsync(ct);
        if (stored is null)
            return null;

        var node = JsonNode.Parse(stored) as JsonObject ?? [];
        return Preferences.FromStored(
            node["theme"]?.GetValueKind() == JsonValueKind.String ? node["theme"]!.GetValue<string>() : null,
            node["mode"]?.GetValueKind() == JsonValueKind.String ? node["mode"]!.GetValue<string>() : null,
            node["chart_patterns"]?.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? node["chart_patterns"]!.GetValue<bool>() : null);
    }

    public async Task SavePreferencesAsync(PersonId id, Preferences preferences, CancellationToken ct)
    {
        var row = await db.Persons.SingleAsync(p => p.Id == id.Value, ct);
        var node = JsonNode.Parse(row.Preferences) as JsonObject ?? [];
        node["theme"] = preferences.Theme;
        node["mode"] = preferences.Mode;
        node["chart_patterns"] = preferences.ChartPatterns;
        row.Preferences = node.ToJsonString();
        await db.SaveChangesAsync(ct);
    }

    private static PersonInfo ToInfo(PersonRow row) => new(
        new PersonId(row.Id),
        row.Kind == "adult" ? PersonKind.Adult : PersonKind.Dependent,
        row.DisplayName,
        row.Email,
        row.SessionEpoch,
        row.IsAdmin,
        row.Kind == "adult" && row.OidcSubject is null);
}

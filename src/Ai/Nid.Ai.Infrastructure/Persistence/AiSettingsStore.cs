using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using NodaTime;

namespace Nid.Ai.Infrastructure.Persistence;

public sealed class AiSettingsStore(AiDbContext db) : IAiSettings
{
    public async Task<bool> RemoteWorkersAllowedAsync(CancellationToken ct)
        => await db.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.RemoteWorkersAllowed).SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyDictionary<string, UseSetting>> UsesAsync(CancellationToken ct)
    {
        var row = await db.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => new { s.Uses, s.Models }).SingleOrDefaultAsync(ct);
        var modes = Map(row?.Uses);
        var models = Map(row?.Models);
        return ModelUses.All.ToDictionary(u => u, u => new UseSetting(
            modes.GetValueOrDefault(u) is { } mode && UseModes.All.Contains(mode) ? mode : UseModes.DefaultFor(u), models.GetValueOrDefault(u)));
    }

    public Task<bool> SetUseModeAsync(string use, string mode, CancellationToken ct)
        => ChangeAsync(s => s.Uses = With(s.Uses, use, mode), ct);

    public Task<bool> SetUseModelAsync(string use, string? model, CancellationToken ct)
        => ChangeAsync(s => s.Models = With(s.Models, use, model), ct);

    private async Task<bool> ChangeAsync(Action<AiSettingsRow> change, CancellationToken ct)
    {
        var settings = await db.Settings.SingleOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is null)
            db.Settings.Add(settings = new AiSettingsRow { Id = 1 });
        change(settings);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private static Dictionary<string, string> Map(string? json)
        => json is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];

    private static string With(string? json, string key, string? value)
    {
        var map = Map(json);
        if (value is null)
            map.Remove(key);
        else
            map[key] = value;
        return JsonSerializer.Serialize(map);
    }

    public async Task<bool> SetRemoteWorkersAllowedAsync(bool allowed, Guid person, Instant at, CancellationToken ct)
    {
        var consent = new ConsentRow { Id = Guid.CreateVersion7(), PersonId = person, Subject = "remote_workers", Value = allowed, At = at };
        db.Consents.Add(consent);
        var settings = await db.Settings.SingleOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is null)
            db.Settings.Add(settings = new AiSettingsRow { Id = 1 });
        settings.RemoteWorkersAllowed = allowed;
        settings.RemoteConsentId = consent.Id;
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // Another adult's change won (the row's version, or the first row inserted twice): nothing of this one is kept.
            db.ChangeTracker.Clear();
            return false;
        }
    }
}

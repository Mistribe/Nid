using Microsoft.EntityFrameworkCore;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Banking.Infrastructure.Persistence;

/// <summary>#513: Banking's settings row on <see cref="BankingDbContext"/>; its migration creates it, a missing one reads as no setting.</summary>
public sealed class BankingSettingsStore(BankingDbContext db) : IBankingSettings
{
    public async Task<BankingSettingsView> ReadAsync(CancellationToken ct)
    {
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new SettingsRow();
        return new BankingSettingsView(row.EnableBankingAppId, row.BankRetentionMonths);
    }

    public async Task<Result<BankingSettingsView>> UpdateAsync(BankingSettingsChange change, CancellationToken ct)
    {
        if (change.BankRetentionMonths is < 1 or > 600)
            return Result.Failure<BankingSettingsView>(FieldErrors.At(
                new Failure(BankingCodes.InvalidBankRetention, "Bank transactions are kept between 1 and 600 months.", null), "bankRetentionMonths"));

        var row = await db.Settings.FirstOrDefaultAsync(ct);
        if (row is null)
            db.Settings.Add(row = new SettingsRow());

        if (change.EnableBankingAppId is { } appId)
            row.EnableBankingAppId = string.IsNullOrWhiteSpace(appId) ? null : appId.Trim();

        if (change.ClearBankRetention)
            row.BankRetentionMonths = null;
        else if (change.BankRetentionMonths is { } months)
            row.BankRetentionMonths = months;

        await db.SaveChangesAsync(ct);
        return Result.Success(await ReadAsync(ct));
    }
}

using Nid.Banking.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.Application;

/// <summary>#513: Banking's settings, read by the admin household route alongside the household's.</summary>
public sealed record GetBankingSettingsQuery : IQuery<BankingSettingsView>, IAdminOnly;

/// <summary>#513: the admin household route's banking fields (<see cref="BankingSettingsChange"/>).</summary>
public sealed record UpdateBankingSettingsCommand(string? EnableBankingAppId = null, int? BankRetentionMonths = null, bool ClearBankRetention = false)
    : ICommand<BankingSettingsView>, IAdminOnly, IRecordsSecurityEvent
{
    public SecurityEvent? EventFor(object response)
    {
        var fields = new[] { EnableBankingAppId is null ? null : "enable_banking_app_id",
            BankRetentionMonths is null && !ClearBankRetention ? null : "bank_retention_months" };
        var joined = string.Join(",", fields.OfType<string>());
        // No field was given: nothing changed, so nothing is worth an event.
        return joined.Length == 0 ? null : new SecurityEvent(SecurityEventNames.HouseholdSettingsChanged, null, [],
            new Dictionary<string, string> { ["fields"] = joined });
    }
}

[RequestHandler<GetBankingSettingsQuery, BankingSettingsView>]
public sealed class GetBankingSettingsHandler(IBankingSettings settings) : IRequestHandler<GetBankingSettingsQuery, BankingSettingsView>
{
    public async ValueTask<Result<BankingSettingsView>> HandleAsync(GetBankingSettingsQuery query, CancellationToken ct = default)
        => Result.Success(await settings.ReadAsync(ct));
}

[RequestHandler<UpdateBankingSettingsCommand, BankingSettingsView>]
public sealed class UpdateBankingSettingsHandler(IBankingSettings settings) : IRequestHandler<UpdateBankingSettingsCommand, BankingSettingsView>
{
    public async ValueTask<Result<BankingSettingsView>> HandleAsync(UpdateBankingSettingsCommand command, CancellationToken ct = default)
    {
        // #414: the installation-wide application is deprecated; one already saved is kept (sent again unchanged) or cleared, never set anew.
        if (command.EnableBankingAppId?.Trim() is { Length: > 0 } wanted && (await settings.ReadAsync(ct)).EnableBankingAppId != wanted)
            return Result.Failure<BankingSettingsView>(FieldErrors.At(new Failure(BankingCodes.InstallationDeprecated,
                "The installation-wide Enable Banking application is deprecated: each adult declares their own in Settings › Banks.", null),
                "enableBankingAppId"));

        return await settings.UpdateAsync(new BankingSettingsChange(command.EnableBankingAppId, command.BankRetentionMonths, command.ClearBankRetention), ct);
    }
}

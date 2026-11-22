using Nid.Ai.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Ai.Application;

/// <summary>The cloud settings and profiles (any adult reads them: what may leave, to whom, under which terms).</summary>
public sealed record GetCloudSettingsQuery : IQuery<CloudSettingsView>;

/// <summary>The household's region for the providers' terms (§4): an admin confirms it.</summary>
public sealed record SetCloudRegionCommand(string Region) : ICommand<CloudSettingsView>, IAdminOnly, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent? EventFor(object response)
        => new(SecurityEventNames.AiCloudRegionSet, null, [], new Dictionary<string, string> { ["region"] = Region });
}

/// <summary>Enables a profile (§12): an admin accepts the terms as reviewed on <paramref name="TermsReviewedOn"/>, the region
/// confirmed, the provider's key entered. Every adult is told.</summary>
public sealed record EnableCloudProfileCommand(string ProfileId, LocalDate TermsReviewedOn)
    : ICommand<CloudSettingsView>, IAdminOnly, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent? EventFor(object response)
        => new(SecurityEventNames.AiCloudProfileEnabled, null, [], new Dictionary<string, string> { ["profile"] = ProfileId });
}

public sealed record DisableCloudProfileCommand(string ProfileId) : ICommand<CloudSettingsView>, IAdminOnly, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent? EventFor(object response)
        => new(SecurityEventNames.AiCloudProfileDisabled, null, [], new Dictionary<string, string> { ["profile"] = ProfileId });
}

/// <summary>Enters or replaces a provider's API key (§10): stored encrypted, never shown again, never in a log, the export or the audit.</summary>
public sealed record SetProviderKeyCommand(string Provider, string Key) : ICommand<CloudSettingsView>, IAdminOnly, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent? EventFor(object response)
        => new(SecurityEventNames.AiProviderKeySet, null, [], new Dictionary<string, string> { ["provider"] = Provider });

    /// <summary>The key never reaches a log through the command's text.</summary>
    public override string ToString() => $"{nameof(SetProviderKeyCommand)} {{ Provider = {Provider} }}";
}

public sealed record RevokeProviderKeyCommand(string Provider) : ICommand<CloudSettingsView>, IAdminOnly, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent? EventFor(object response)
        => new(SecurityEventNames.AiProviderKeyRevoked, null, [], new Dictionary<string, string> { ["provider"] = Provider });
}

public static class CloudCodes
{
    public const string UnknownProfile = "ai.unknown_profile";
    public const string UnknownRegion = "ai.unknown_region";
    public const string RegionUnset = "ai.region_unset";
    public const string NotEligible = "ai.profile_not_eligible";
    public const string TermsChanged = "ai.terms_changed";
    public const string NoKey = "ai.no_key";
    public const string InvalidKey = "ai.invalid_key";
}

internal static class CloudViews
{
    /// <summary>Terms reviewed more than 90 days ago are flagged (§4).</summary>
    public static readonly Period ReviewFor = Period.FromDays(90);

    public static async Task<CloudSettingsView> ReadAsync(ILlmProfiles profiles, ICloudSettings settings, CloudBreaker breaker, IClock clock, CancellationToken ct)
    {
        var region = await settings.RegionAsync(ct);
        var enabled = (await settings.EnabledAsync(ct)).ToDictionary(e => e.ProfileId, StringComparer.Ordinal);
        var hints = await settings.KeyHintsAsync(ct);
        var today = clock.GetCurrentInstant().InUtc().Date;
        return new CloudSettingsView(region, [.. profiles.All.Select(p => new CloudProfileView(
            p.Id, p.Provider, p.Tier, p.Host, p.Model,
            region is not null ? !p.EligibleIn(region) : p.DataTerms.TrainsOnData.Values.Any(v => v),
            p.DataTerms.HumanReview, p.DataTerms.RetentionDays, p.DataTerms.TermsUrl, p.DataTerms.ReviewedOn,
            ReviewDue: p.DataTerms.ReviewedOn + ReviewFor < today,
            Eligible: region is not null && p.EligibleIn(region),
            Enabled: enabled.TryGetValue(p.Id, out var accepted) && accepted.AcceptedReviewedOn == p.DataTerms.ReviewedOn,
            KeyHint: hints.GetValueOrDefault(p.Provider),
            Suspended: breaker.IsSuspended(p.Id)))]);
    }
}

[RequestHandler<GetCloudSettingsQuery, CloudSettingsView>]
public sealed class GetCloudSettingsHandler(ILlmProfiles profiles, ICloudSettings settings, CloudBreaker breaker, IClock clock)
    : IRequestHandler<GetCloudSettingsQuery, CloudSettingsView>
{
    public async ValueTask<Result<CloudSettingsView>> HandleAsync(GetCloudSettingsQuery query, CancellationToken ct = default)
        => Result.Success(await CloudViews.ReadAsync(profiles, settings, breaker, clock, ct));
}

[RequestHandler<SetCloudRegionCommand, CloudSettingsView>]
public sealed class SetCloudRegionHandler(ILlmProfiles profiles, ICloudSettings settings, CloudBreaker breaker, IClock clock)
    : IRequestHandler<SetCloudRegionCommand, CloudSettingsView>
{
    public async ValueTask<Result<CloudSettingsView>> HandleAsync(SetCloudRegionCommand command, CancellationToken ct = default)
    {
        if (!CloudRegions.All.Contains(command.Region))
            return Result.Failure<CloudSettingsView>(new Failure(CloudCodes.UnknownRegion, "eea_ch_uk or other.", null));
        await settings.SetRegionAsync(command.Region, ct);
        // A profile whose terms train on the data in the new region is no longer used: it is disabled.
        foreach (var enabled in await settings.EnabledAsync(ct))
            if (profiles.All.FirstOrDefault(p => p.Id == enabled.ProfileId) is { } profile && !profile.EligibleIn(command.Region))
                await settings.DisableAsync(profile.Id, ct);
        return Result.Success(await CloudViews.ReadAsync(profiles, settings, breaker, clock, ct));
    }
}

[RequestHandler<EnableCloudProfileCommand, CloudSettingsView>]
public sealed class EnableCloudProfileHandler(
    ILlmProfiles profiles, ICloudSettings settings, CloudBreaker breaker, IActorAccessor actors, INotifier notifier, IClock clock)
    : IRequestHandler<EnableCloudProfileCommand, CloudSettingsView>
{
    public async ValueTask<Result<CloudSettingsView>> HandleAsync(EnableCloudProfileCommand command, CancellationToken ct = default)
    {
        if (actors.Current?.PersonId is not { } person)
            return Result.Failure<CloudSettingsView>(new UnauthorizedFailure("An admin enables a profile.", null));
        if (profiles.All.FirstOrDefault(p => p.Id == command.ProfileId) is not { } profile)
            return Result.Failure<CloudSettingsView>(new Failure(CloudCodes.UnknownProfile, "No such profile.", null));
        if (await settings.RegionAsync(ct) is not { } region)
            return Result.Failure<CloudSettingsView>(new Failure(CloudCodes.RegionUnset, "Confirm the household's region first.", null));
        if (!profile.EligibleIn(region))
            return Result.Failure<CloudSettingsView>(new Failure(CloudCodes.NotEligible, "Its terms train on the data in this region.", null));
        if (command.TermsReviewedOn != profile.DataTerms.ReviewedOn)
            return Result.Failure<CloudSettingsView>(new ConflictFailure("The terms were reviewed again: read them again.") { Code = CloudCodes.TermsChanged });
        if (!(await settings.KeyHintsAsync(ct)).ContainsKey(profile.Provider))
            return Result.Failure<CloudSettingsView>(new Failure(CloudCodes.NoKey, "Enter the provider's key first.", null));

        var now = clock.GetCurrentInstant();
        var wasEnabled = (await settings.EnabledAsync(ct)).Any(e => e.ProfileId == profile.Id && e.AcceptedReviewedOn == profile.DataTerms.ReviewedOn);
        await settings.EnableAsync(profile.Id, profile.DataTerms.ReviewedOn, person.Value, now, ct);
        if (!wasEnabled)
            await DataPolicyNotices.TellAsync(notifier, "cloud_profile_on", null, now, ct, new Dictionary<string, string> { ["profile"] = ProfileName(profile) });
        return Result.Success(await CloudViews.ReadAsync(profiles, settings, breaker, clock, ct));
    }

    internal static string ProfileName(LlmProfile profile) => $"{profile.Provider} {profile.Model}";
}

[RequestHandler<DisableCloudProfileCommand, CloudSettingsView>]
public sealed class DisableCloudProfileHandler(ILlmProfiles profiles, ICloudSettings settings, CloudBreaker breaker, INotifier notifier, IClock clock)
    : IRequestHandler<DisableCloudProfileCommand, CloudSettingsView>
{
    public async ValueTask<Result<CloudSettingsView>> HandleAsync(DisableCloudProfileCommand command, CancellationToken ct = default)
    {
        if (profiles.All.FirstOrDefault(p => p.Id == command.ProfileId) is not { } profile)
            return Result.Failure<CloudSettingsView>(new Failure(CloudCodes.UnknownProfile, "No such profile.", null));
        if (await settings.DisableAsync(profile.Id, ct))
            await DataPolicyNotices.TellAsync(notifier, "cloud_profile_off", null, clock.GetCurrentInstant(), ct,
                new Dictionary<string, string> { ["profile"] = EnableCloudProfileHandler.ProfileName(profile) });
        return Result.Success(await CloudViews.ReadAsync(profiles, settings, breaker, clock, ct));
    }
}

[RequestHandler<SetProviderKeyCommand, CloudSettingsView>]
public sealed class SetProviderKeyHandler(ILlmProfiles profiles, ICloudSettings settings, CloudBreaker breaker, IActorAccessor actors, IClock clock)
    : IRequestHandler<SetProviderKeyCommand, CloudSettingsView>
{
    public async ValueTask<Result<CloudSettingsView>> HandleAsync(SetProviderKeyCommand command, CancellationToken ct = default)
    {
        if (actors.Current?.PersonId is not { } person)
            return Result.Failure<CloudSettingsView>(new UnauthorizedFailure("An admin enters a key.", null));
        if (!profiles.All.Any(p => p.Provider == command.Provider))
            return Result.Failure<CloudSettingsView>(new Failure(CloudCodes.UnknownProfile, "No profile uses this provider.", null));
        var key = command.Key.Trim();
        if (key.Length is < 16 or > 512 || key.Any(char.IsWhiteSpace))
            return Result.Failure<CloudSettingsView>(new Failure(CloudCodes.InvalidKey, "This does not look like an API key.", null));

        await settings.SetKeyAsync(command.Provider, key, person.Value, clock.GetCurrentInstant(), ct);
        return Result.Success(await CloudViews.ReadAsync(profiles, settings, breaker, clock, ct));
    }
}

[RequestHandler<RevokeProviderKeyCommand, CloudSettingsView>]
public sealed class RevokeProviderKeyHandler(ILlmProfiles profiles, ICloudSettings settings, CloudBreaker breaker, INotifier notifier, IClock clock)
    : IRequestHandler<RevokeProviderKeyCommand, CloudSettingsView>
{
    public async ValueTask<Result<CloudSettingsView>> HandleAsync(RevokeProviderKeyCommand command, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        await settings.RevokeKeyAsync(command.Provider, now, ct);
        // Without its key a profile cannot be used: it is disabled, and every adult told.
        foreach (var enabled in await settings.EnabledAsync(ct))
            if (profiles.All.FirstOrDefault(p => p.Id == enabled.ProfileId) is { } profile && profile.Provider == command.Provider
                && await settings.DisableAsync(profile.Id, ct))
                await DataPolicyNotices.TellAsync(notifier, "cloud_profile_off", null, now, ct,
                    new Dictionary<string, string> { ["profile"] = EnableCloudProfileHandler.ProfileName(profile) });
        return Result.Success(await CloudViews.ReadAsync(profiles, settings, breaker, clock, ct));
    }
}

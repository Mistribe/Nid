using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain.Catalog;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Subscriptions.Application.Catalog;

/// <summary><c>GET /providers?q=</c>: the providers visible to the actor, filtered by name when <paramref name="Q"/> is given.</summary>
public sealed record ListProvidersQuery(string? Q) : IQuery<IReadOnlyList<ProviderView>>;

/// <summary><c>GET /labels</c>: every label, reference data.</summary>
public sealed record ListLabelsQuery : IQuery<IReadOnlyList<LabelView>>;

/// <summary><c>POST /providers</c>: a custom provider with a generated key (design §3.3).</summary>
public sealed record CreateProviderCommand(string? Name, string? Website, string? PricingPage, IReadOnlyList<string> LabelKeys)
    : ICommand<ProviderView>;

[RequestHandler<ListProvidersQuery, IReadOnlyList<ProviderView>>]
public sealed class ListProvidersHandler(ProviderAccess access, IProviderStore providers, IActorAccessor actor)
    : IRequestHandler<ListProvidersQuery, IReadOnlyList<ProviderView>>
{
    public async ValueTask<Result<IReadOnlyList<ProviderView>>> HandleAsync(ListProvidersQuery query, CancellationToken ct = default)
    {
        // As CreateProviderHandler: the providers are a person's (final review F2).
        if (actor.Current?.PersonId is not { } person)
            return Result.Failure<IReadOnlyList<ProviderView>>(new Failure(CatalogCodes.InvalidProvider, "Providers are listed for a person.", null));

        var visible = await access.ListAsync(person, query.Q, ct);
        var labels = await ProviderViews.LabelsAsync(providers, ct);
        var included = await ProviderViews.IncludedAsync(providers, visible, ct);
        return Result.Success<IReadOnlyList<ProviderView>>(visible.Select(p => ProviderView.From(p, labels, included)).ToList());
    }
}

[RequestHandler<ListLabelsQuery, IReadOnlyList<LabelView>>]
public sealed class ListLabelsHandler(IProviderStore providers) : IRequestHandler<ListLabelsQuery, IReadOnlyList<LabelView>>
{
    public async ValueTask<Result<IReadOnlyList<LabelView>>> HandleAsync(ListLabelsQuery query, CancellationToken ct = default)
        => Result.Success<IReadOnlyList<LabelView>>((await providers.LabelsAsync(ct)).Select(LabelView.From).ToList());
}

[RequestHandler<CreateProviderCommand, ProviderView>]
public sealed class CreateProviderHandler(IProviderStore providers, ProviderKeys keys, IActorAccessor actor)
    : IRequestHandler<CreateProviderCommand, ProviderView>
{
    /// <summary>A generated key checked free can still lose a race with another insert: draw again, a few times.</summary>
    private const int Attempts = 3;

    public async ValueTask<Result<ProviderView>> HandleAsync(CreateProviderCommand command, CancellationToken ct = default)
    {
        if (actor.Current?.PersonId is not { } person)
            return Invalid("A custom provider is created by a person.");

        var name = ProviderRules.Name(command.Name, CatalogLimits.MaxNameLength);
        if (name is null)
            return Invalid($"A provider has a name of 1 to {CatalogLimits.MaxNameLength} characters.");

        var website = ProviderRules.Url(command.Website);
        var pricingPage = ProviderRules.Url(command.PricingPage);
        if (!ProviderRules.IsUrl(website) || !ProviderRules.IsUrl(pricingPage))
            return Invalid($"A website or pricing page is an http or https address of at most {CatalogLimits.MaxUrlLength} characters.");

        // HTTP refuses a null list or element before the command (RequireLabelKeys); any other caller gets a refusal
        // here, never an exception (final review F2). A null list is an empty one.
        var labelKeys = (command.LabelKeys ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (labelKeys.Count == 0)
            return Invalid("A provider has at least one label.");
        if (labelKeys.Any(string.IsNullOrWhiteSpace))
            return Invalid("A label key is never empty.");
        var labels = await ProviderViews.LabelsAsync(providers, ct);
        if (labelKeys.FirstOrDefault(key => !labels.ContainsKey(key)) is { } unknown)
            return Invalid($"'{unknown}' is not a label.");

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var provider = new Provider(
                await keys.NewAsync(name, ct), ProviderOrigin.Custom, name, website, pricingPage, labelKeys, null, person, null, null);
            var created = await providers.CreateAsync(provider, ct);
            if (!created.TryGetFailure(out var failure))
                return Result.Success(ProviderView.From(provider, labels, ProviderViews.NoIncluded));
            if (failure!.Code != CatalogCodes.KeyTaken)
                return Result.Failure<ProviderView>(failure);
        }

        return Invalid("No free key could be generated for this name; try again.");
    }

    private static Result<ProviderView> Invalid(string message)
        => Result.Failure<ProviderView>(new Failure(CatalogCodes.InvalidProvider, message, null));
}

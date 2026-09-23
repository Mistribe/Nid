using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform;

/// <summary>The modules this build enables; the SPA hides what is not listed (frontend/architecture.md §2).</summary>
public sealed record EnabledModules(IReadOnlyList<string> Modules);

public sealed record CapabilitiesView(IReadOnlyList<string> Modules);

public sealed record GetCapabilitiesQuery : IQuery<CapabilitiesView>;

[RequestHandler<GetCapabilitiesQuery, CapabilitiesView>]
public sealed class GetCapabilitiesHandler(EnabledModules modules) : IRequestHandler<GetCapabilitiesQuery, CapabilitiesView>
{
    public ValueTask<Result<CapabilitiesView>> HandleAsync(GetCapabilitiesQuery query, CancellationToken ct = default)
        => ValueTask.FromResult(Result.Success(new CapabilitiesView(modules.Modules)));
}

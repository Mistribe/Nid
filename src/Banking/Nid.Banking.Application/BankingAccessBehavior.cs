using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.Application;

/// <summary>
/// #516, architecture.md §5.4: the « banking on » switch (<see cref="IRequiresBanking"/>), then the account (<see cref="IBankAccountScoped"/>)
/// or the line (<see cref="IBankLineScoped"/>) a request names, checked here rather than in each handler, with the failures the handlers
/// answered. The checked account or line goes to the handler through the request context (<see cref="CheckedResources"/>). Declared by the
/// host (<c>SynapseGlobalBehavior</c>); its services are resolved only for a banking request.
/// </summary>
public sealed class BankingAccessBehavior<TRequest, TResponse>(IServiceProvider services, IContextAccessor context)
    : IRequestPipelineBehavior<TRequest, TResponse>, IOrderedPipelineBehavior
    where TRequest : IRequest<TResponse>
    where TResponse : notnull
{
    public uint Order => ModuleBehaviorOrder.ResourceAccess;

    public async ValueTask<Result<TResponse>> HandleAsync(
        TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken = default)
    {
        if (request is not IRequiresBanking)
            return await next(request, cancellationToken);

        var access = services.GetRequiredService<BankingAccess>();
        if ((await access.EnabledAsync(cancellationToken)).TryGetFailure(out var off))
            return Result.Failure<TResponse>(off!);

        Result<object>? found = null;
        if (request is IBankAccountScoped account)
            found = (await access.AccountAsync(account.BankAccountId, account.Access, cancellationToken)).Map(a => (object)a);
        else if (request is IBankLineScoped line)
            found = (await services.GetRequiredService<BankLines>().FindAsync(line.TransactionId, line.Access, cancellationToken)).Map(l => (object)l);
        if (found is { } scoped)
        {
            if (!scoped.TryGetValue(out var resource))
                return Result.Failure<TResponse>(scoped.TryGetFailure(out var refused) ? refused! : throw new InvalidOperationException());
            CheckedResources.Hand(context, request, resource!);
        }
        return await next(request, cancellationToken);
    }
}

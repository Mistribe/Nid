using Microsoft.Extensions.Logging.Abstractions;
using Nid.Core.Contracts;
using Nid.Host;
using Nid.Investments.Application;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Documents.IntegrationTests;

/// <summary>#108: the mask never withholds the figures, even when the readability check throws.</summary>
public class DocumentMaskBehaviorTests
{
    private sealed class ThrowingInvoker : IInvoker
    {
        ValueTask<Result<TResponse>> IInvoker.InvokeAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Documents is down.");

        ValueTask<Result> IInvoker.InvokeAsync<TRequest>(TRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Documents is down.");

        IAsyncEnumerable<Result<TItem>> IInvoker.InvokeStreamAsync<TItem>(IStreamRequest<TItem> request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Documents is down.");
    }

    [Fact]
    public async Task A_check_that_throws_hides_every_id_and_keeps_the_response()
    {
        var document = Guid.NewGuid();
        var amount = new AmountView("100.00", "EUR");
        var detail = new EnvelopeDetail(new ItemId(Guid.NewGuid()), "PEA", "pea", "EUR", "shared", false, true, null, null, null, "monthly", null, null,
            null, null, null!, [new ValuationView(Guid.NewGuid(), new LocalDate(2025, 9, 30), amount, null, document)],
            [new FlowView(Guid.NewGuid(), new LocalDate(2025, 9, 5), "contribution", amount, null, null, null, document)], [], "view", false);
        var behavior = new DocumentMaskBehavior<GetEnvelopeQuery, EnvelopeDetail>(
            new ThrowingInvoker(), NullLogger<DocumentMaskBehavior<GetEnvelopeQuery, EnvelopeDetail>>.Instance);

        var result = await behavior.HandleAsync(new GetEnvelopeQuery(detail.Id), (_, _) => ValueTask.FromResult(Result.Success(detail)), TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out var masked));
        Assert.Null(Assert.Single(masked!.Valuations).DocumentId);
        Assert.Null(Assert.Single(masked.Flows).DocumentId);
    }
}

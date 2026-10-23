using Nid.RealEstate.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.TestSupport;

/// <summary>Serves <see cref="Body"/> as the INSEE series (a test sets it from <c>tests/vectors/irl/</c>); never touches the network.</summary>
public sealed class FakeIrlSource : IIrlSourceClient
{
    public byte[] Body { get; set; } = [];

    /// <summary>When set, the request fails with this code.</summary>
    public string? FailWith { get; set; }

    public int Requests;

    /// <summary>What every test host uses unless a test installs its own: the source is unavailable.</summary>
    public static FakeIrlSource Offline() => new() { FailWith = IrlCodes.SourceUnavailable };

    public Task<Result<byte[]>> FetchAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref Requests);
        return Task.FromResult(FailWith is { } code
            ? Result.Failure<byte[]>(new Failure(code, "Fake source failure.", null))
            : Result.Success(Body));
    }
}

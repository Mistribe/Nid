using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Tests;

internal static class ResultAssertions
{
    /// <summary>Returns the value of a successful result, or fails the test with the failure message.</summary>
    public static T Ok<T>(this Result<T> result) where T : notnull
    {
        Assert.True(result.TryGetValue(out var value), result.ToString());
        return value!;
    }

    /// <summary>Returns the failure of a failed result, or fails the test if it is successful.</summary>
    public static Failure Fail<T>(this Result<T> result) where T : notnull
    {
        Assert.True(result.TryGetFailure(out var failure), "Expected a failure, but the result was successful.");
        return failure!;
    }
}

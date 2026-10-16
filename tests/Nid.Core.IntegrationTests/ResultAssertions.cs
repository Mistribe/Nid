using UnambitiousFx.Functional;

namespace Nid.Core.IntegrationTests;

internal static class ResultAssertions
{
    /// <summary>Returns the value of a successful result, or fails the test with the failure message.</summary>
    public static T Ok<T>(this Result<T> result) where T : notnull
    {
        Assert.True(result.TryGetValue(out var value), result.ToString());
        return value!;
    }

    /// <summary>Returns the failure of a failed result, or fails the test.</summary>
    public static UnambitiousFx.Functional.Failures.Failure Fails<T>(this Result<T> result) where T : notnull
    {
        Assert.True(result.TryGetFailure(out var failure), "Expected a failure.");
        return failure!;
    }
}

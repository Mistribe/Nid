using UnambitiousFx.Functional;

namespace Nid.TestSupport;

public static class ResultAssertions
{
    /// <summary>Returns the value of a successful result, or fails the test with the failure message.</summary>
    public static T Ok<T>(this Result<T> result) where T : notnull
    {
        Assert.True(result.TryGetValue(out var value), result.ToString());
        return value!;
    }

    public static void Ok(this Result result)
        => Assert.True(!result.TryGetFailure(out var failure), failure?.ToString() ?? "");

    /// <summary>Returns the failure of a failed result, or fails the test.</summary>
    public static UnambitiousFx.Functional.Failures.Failure Fails<T>(this Result<T> result) where T : notnull
    {
        Assert.True(result.TryGetFailure(out var failure), "Expected a failure.");
        return failure!;
    }

    public static string? FailureCode<T>(this Result<T> result) where T : notnull
        => result.TryGetFailure(out var failure) ? failure!.Code : null;

    public static string? FailureCode(this Result result)
        => result.TryGetFailure(out var failure) ? failure!.Code : null;
}

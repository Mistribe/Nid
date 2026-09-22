using UnambitiousFx.Functional;

namespace Nid.Subscriptions.Tests;

internal static class ResultAssertions
{
    /// <summary>Returns the value of a successful result, or fails the test with the failure message.</summary>
    public static T Ok<T>(this Result<T> result) where T : notnull
    {
        Assert.True(result.TryGetValue(out var value), result.ToString());
        return value!;
    }

    /// <summary>Returns the failure code of a failed result, or null if it succeeded.</summary>
    public static string? FailureCode<T>(this Result<T> result) where T : notnull
        => result.TryGetFailure(out var failure) ? failure!.Code : null;
}

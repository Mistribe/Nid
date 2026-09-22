using UnambitiousFx.Functional;

namespace Nid.Subscriptions.IntegrationTests;

internal static class ResultAssertions
{
    public static T Ok<T>(this Result<T> result) where T : notnull
    {
        Assert.True(result.TryGetValue(out var value), result.ToString());
        return value!;
    }

    public static void Ok(this Result result)
        => Assert.True(!result.TryGetFailure(out var failure), failure?.ToString() ?? "");

    public static string? FailureCode<T>(this Result<T> result) where T : notnull
        => result.TryGetFailure(out var failure) ? failure!.Code : null;

    public static string? FailureCode(this Result result)
        => result.TryGetFailure(out var failure) ? failure!.Code : null;
}

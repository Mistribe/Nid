using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Ai.IntegrationTests;

/// <summary>A model call that was refused or failed, raised by the tests so they can assert on its code and on what came
/// before it.</summary>
internal sealed class ModelRefused(string code, IReadOnlyList<object>? before = null) : Exception(code)
{
    public string Code { get; } = code;

    public IReadOnlyList<object> Before { get; } = before ?? [];
}

internal static class ModelResults
{
    public static T OrThrow<T>(this Result<T> result) where T : notnull
        => result.TryGet(out var value, out var failure) ? value : throw new ModelRefused(failure!.Code);

    public static async Task<T> OrThrow<T>(this Task<Result<T>> result) where T : notnull => (await result).OrThrow();

    /// <summary>A model stream read to its end; a failure must be its last item, and is raised with what came before it.</summary>
    public static async Task<List<T>> ReadAllAsync<T>(this IAsyncEnumerable<Result<T>> stream) where T : notnull
    {
        var values = new List<T>();
        Failure? failure = null;
        await foreach (var item in stream)
        {
            Assert.Null(failure); // nothing after a failure
            if (item.TryGet(out var value, out var failed))
                values.Add(value);
            else
                failure = failed;
        }

        return failure is null ? values : throw new ModelRefused(failure.Code, [.. values.Cast<object>()]);
    }
}

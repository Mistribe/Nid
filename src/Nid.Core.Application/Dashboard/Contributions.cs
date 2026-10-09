using UnambitiousFx.Functional;

namespace Nid.Core.Application.Dashboard;

/// <summary>Reads each registered module's part; the first failure fails the whole read.</summary>
internal static class Contributions
{
    internal static async Task<Result<IReadOnlyList<T>>> CollectAsync<TSource, T>(IEnumerable<TSource> sources, Func<TSource, Task<Result<T>>> read)
        where T : notnull
    {
        var parts = new List<T>();
        foreach (var source in sources)
        {
            var part = await read(source);
            if (part.TryGetFailure(out var failure)) return Result.Failure<IReadOnlyList<T>>(failure!);
            part.TryGetValue(out var value);
            parts.Add(value!);
        }
        return Result.Success<IReadOnlyList<T>>(parts);
    }
}

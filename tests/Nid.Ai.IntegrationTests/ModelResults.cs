using UnambitiousFx.Functional;

namespace Nid.Ai.IntegrationTests;

/// <summary>A model call that was refused or failed, raised by the tests so they can assert on its code.</summary>
internal sealed class ModelRefused(string code) : Exception(code)
{
    public string Code { get; } = code;
}

internal static class ModelResults
{
    public static T OrThrow<T>(this Result<T> result) where T : notnull
        => result.TryGet(out var value, out var failure) ? value : throw new ModelRefused(failure!.Code);

    public static async Task<T> OrThrow<T>(this Task<Result<T>> result) where T : notnull => (await result).OrThrow();
}

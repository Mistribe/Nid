using System.Text.Json;
using Nid.Platform.Contracts;

namespace Nid.TestSupport;

public static class ProblemJson
{
    /// <summary>
    /// #250: the path a refused field of a Problem Details' <c>errors</c> names (<c>payload.movements[3].amount</c>), read back
    /// from its RFC 9457 <c>pointer</c>; empty for an error tied to no field. An entry of another list (a proposal's
    /// warning, an import issue) keeps its own <c>path</c>.
    /// </summary>
    public static string FieldPath(this JsonElement entry)
        => entry.TryGetProperty("pointer", out var pointer)
            ? FieldErrors.PathOf(pointer.GetString())
            : entry.TryGetProperty("path", out var path) ? path.GetString() ?? "" : "";

    /// <summary>A refused field's RFC 9457 <c>pointer</c> as sent, for a path a pointer cannot tell from an index (a year key).</summary>
    public static string? FieldPointer(this JsonElement entry)
        => entry.TryGetProperty("pointer", out var pointer) ? pointer.GetString() : null;

    /// <summary>The pointer the API sends for a rule's path (<c>inflationByYear.2031</c> → <c>#/inflationByYear/2031</c>).</summary>
    public static string? PointerOf(string path) => new UnambitiousFx.Functional.Failures.ValidationError(path, "").Pointer;
}

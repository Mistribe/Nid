using Microsoft.EntityFrameworkCore;
using Npgsql;
using UnambitiousFx.Functional.Failures;

namespace Nid.Host.Api;

/// <summary>
/// #630: a write the database refused because another one got there first — a stale <c>xmin</c> or a unique index taken by a
/// concurrent insert — answers the 409 <see cref="Code"/> (architecture §6) instead of a 500, on the API and on MCP calls alike.
/// A store that knows its own conflict still answers its own code; this is the floor for those that do not.
/// </summary>
public static class WriteConflicts
{
    public const string Code = "core.conflict";

    public static bool Is(Exception e) => e is DbUpdateConcurrencyException
        or DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } };

    public static ConflictFailure Failure() => new("Another change was saved at the same time: reload and try again.") { Code = Code };

    public static IApplicationBuilder UseWriteConflicts(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        try
        {
            await next(context);
        }
        catch (Exception e) when (Is(e) && !context.Response.HasStarted)
        {
            await ResultHttp.Problem(Failure()).ExecuteAsync(context);
        }
    });
}

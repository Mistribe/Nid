using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Host.Api;
using Npgsql;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>#630: a write the database refuses for a concurrent one answers the 409 <c>core.conflict</c>, not a 500.</summary>
public class WriteConflictsTests
{
    private static async Task<(int Status, string? Code)> AnswerAsync(Exception thrown)
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseWriteConflicts();
        app.Run(_ => throw thrown);
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();

        await app.Build()(context);

        context.Response.Body.Position = 0;
        var body = JsonDocument.Parse(context.Response.Body).RootElement;
        return (context.Response.StatusCode, body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_stale_version_and_a_taken_unique_index_answer_409()
    {
        Assert.Equal((409, WriteConflicts.Code), await AnswerAsync(new DbUpdateConcurrencyException("stale")));
        var unique = new PostgresException("duplicate key", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation);
        Assert.Equal((409, WriteConflicts.Code), await AnswerAsync(new DbUpdateException("insert", unique)));
    }

    [Fact]
    public async Task Any_other_failure_is_left_alone()
        => await Assert.ThrowsAsync<InvalidOperationException>(() => AnswerAsync(new InvalidOperationException("bug")));
}

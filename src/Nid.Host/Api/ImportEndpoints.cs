using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Nid.Core.Contracts;
using Nid.Platform.Import;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Api;

public static class ImportEndpoints
{
    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/platform/imports").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapPost("", async (StartImportBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(body.ToCommand(), ct)).ToHttp(v => v, StatusCodes.Status201Created));

        api.MapGet("", async (IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListImportsQuery(), ct)).ToHttp(v => v));

        api.MapGet("/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetImportQuery(id), ct)).ToHttp(v => v));

        api.MapDelete("/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new CancelImportCommand(id), ct)).ToHttp(v => v));

        // The body is read by hand: its size and depth are bounded before anything parses it (§11.5).
        api.MapPut("/{id:guid}/batches/{section}/{n:int}", async (
                Guid id, string section, int n, HttpRequest request, [FromServices] ImportOptions options, IInvoker invoker, CancellationToken ct) =>
            {
                var body = await BatchBody.ReadAsync(request, options, ct);
                return (await invoker.InvokeAsync(new PutBatchCommand(id, section, n, body.Hash, body.Entities), ct)).ToHttp(v => v);
            });

        return routes;
    }

    public sealed record StartImportBody(string Format, int Version, IReadOnlyDictionary<string, Guid>? Adults)
    {
        public StartImportCommand ToCommand() => new(
            Format, Version, (Adults ?? new Dictionary<string, Guid>()).ToDictionary(a => a.Key, a => new PersonId(a.Value)));
    }

    internal sealed record BatchBody(string Hash, JsonElement Entities)
    {
        public static async Task<BatchBody> ReadAsync(HttpRequest request, ImportOptions options, CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81_920];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > options.MaxBatchBytes)
                    throw new BadHttpRequestException($"A batch is at most {options.MaxBatchBytes} bytes.");
                buffer.Write(chunk, 0, read);
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = options.MaxDepth });
            }
            catch (JsonException e)
            {
                throw new BadHttpRequestException($"The batch is not JSON of at most {options.MaxDepth} levels: {e.Message}");
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("hash", out var hash) || hash.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("entities", out var entities))
                    throw new BadHttpRequestException("A batch is { \"hash\": \"<sha-256>\", \"entities\": [ ... ] }.");

                return new BatchBody(hash.GetString()!, entities.Clone());
            }
        }
    }
}

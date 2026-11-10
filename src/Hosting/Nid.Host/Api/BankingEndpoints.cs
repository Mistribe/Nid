using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Nid.Banking.Application;
using Nid.Banking.Application.Files;
using Nid.Banking.Application.Sync;
using Nid.Banking.Contracts;
using Nid.Banking.Domain.Files;
using Nid.Core.Contracts;
using Nid.Host.Auth;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Host.Api.SubscriptionEndpoints;

namespace Nid.Host.Api;

/// <summary>Bank sync spec §3.1 and §8: the banks, the person's connections, the bank's callback and the bank accounts.</summary>
public static class BankingEndpoints
{
    public const string CallbackPath = "/api/v1/banking/callback";

    /// <summary>Where the browser lands after the bank, success or refusal; the page reads <c>connected</c> or <c>error</c>.</summary>
    private const string BanksPage = "/settings/banks";

    public static IEndpointRouteBuilder MapBankingEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/banking").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("/status", async (IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetBankingStatusQuery(), ct)).ToHttp(status => status))
            .Returns<BankingStatusView>();

        api.MapGet("/aspsps", async (IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListAspspsQuery(), ct)).ToHttp(banks => banks))
            .Returns<IReadOnlyList<AspspView>>();

        api.MapPost("/connections", async (ConnectBody body, IOptions<NidAuthOptions> auth, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new StartConnectionCommand(body.AspspName ?? "", Callback(auth)), ct))
                .ToHttp(started => started, StatusCodes.Status201Created))
            .Returns<ConnectionStarted>(StatusCodes.Status201Created);

        api.MapPost("/connections/{id:guid}/renew", async (Guid id, IOptions<NidAuthOptions> auth, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new RenewConnectionCommand(id, Callback(auth)), ct)).ToHttp(started => started))
            .Returns<ConnectionStarted>();

        // The bank sends the browser back here: a navigation, answered by a redirect to the page, never by a body.
        api.MapGet("/callback", async (string? state, string? code, string? error, IInvoker invoker, CancellationToken ct) =>
            {
                var result = await invoker.InvokeAsync(new CompleteConnectionCommand(state ?? "", code, error), ct);
                return result.TryGetValue(out var id)
                    ? Results.Redirect($"{BanksPage}?connected={id}")
                    : Results.Redirect($"{BanksPage}?error={Uri.EscapeDataString(result.TryGetFailure(out var failure) ? failure!.Code : "ERROR")}");
            })
            .ExcludeFromDescription();

        api.MapGet("/connections", async (IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListMyConnectionsQuery(), ct)).ToHttp(connections => connections))
            .Returns<IReadOnlyList<ConnectionView>>();

        api.MapDelete("/connections/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct) =>
            {
                // A 204 carries no body: ToHttp would write one.
                var result = await invoker.InvokeAsync(new DeleteConnectionCommand(id), ct);
                return result.TryGetValue(out _) ? Results.NoContent() : result.ToHttp(v => v);
            })
            .ReturnsNoContent();

        api.MapGet("/accounts", async (Guid? envelopeId, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListBankAccountsQuery(envelopeId is { } e ? new ItemId(e) : null), ct)).ToHttp(accounts => accounts))
            .Returns<IReadOnlyList<BankAccountView>>();

        api.MapPatch("/accounts/{id:guid}", async (Guid id, BankAccountBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new UpdateBankAccountCommand(
                    id, body.EnvelopeId, body.ClearEnvelope ?? false, body.CardOfAccountId, body.ClearCard ?? false, body.AutoApplyBalance, body.Ignored), ct))
                .ToHttp(x => new IdResponse(x)))
            .Returns<IdResponse>();

        api.MapGet("/envelopes/{envelopeId:guid}/transactions",
                async (Guid envelopeId, string? from, string? to, int? page, string? state, IInvoker invoker, CancellationToken ct)
                    => (await invoker.InvokeAsync(new ListBankTransactionsQuery(
                            new ItemId(envelopeId), OptionalDate(from), OptionalDate(to), page ?? 1, state), ct))
                        .ToHttp(transactions => transactions))
            .Returns<BankTransactionPage>();

        // 5C spec §8: the lines of every account the person reads, their links, the charges they may pay, and the person's rules.
        api.MapGet("/transactions", async (string? state, string? from, string? to, int? page, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new ListBankLinesQuery(state, OptionalDate(from), OptionalDate(to), page ?? 1), ct))
                    .ToHttp(transactions => transactions))
            .Returns<BankTransactionPage>();

        api.MapGet("/transactions/{id:guid}/candidates", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new LinkCandidatesQuery(id), ct)).ToHttp(charges => charges))
            .Returns<IReadOnlyList<ExpectedChargeView>>();

        api.MapGet("/expected-charges", async (string from, string to, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListExpectedChargesQuery(RequireDate(from), RequireDate(to)), ct)).ToHttp(charges => charges))
            .Returns<IReadOnlyList<ExpectedChargeView>>();

        api.MapPost("/transactions/{id:guid}/link", async (Guid id, LinkBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new LinkTransactionCommand(id, body.Module ?? "", body.ItemId ?? Guid.Empty, body.Key), ct)).ToHttp(line => line))
            .Returns<BankTransactionView>();

        api.MapPost("/transactions/{id:guid}/unlink", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new UnlinkTransactionCommand(id), ct)).ToHttp(line => line))
            .Returns<BankTransactionView>();

        api.MapPost("/transactions/{id:guid}/ignore", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new IgnoreTransactionCommand(id), ct)).ToHttp(line => line))
            .Returns<BankTransactionView>();

        api.MapGet("/rules", async (IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListBankRulesQuery(), ct)).ToHttp(rules => rules))
            .Returns<IReadOnlyList<BankRuleView>>();

        api.MapDelete("/rules/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct) =>
            {
                var result = await invoker.InvokeAsync(new DeleteBankRuleCommand(id), ct);
                return result.TryGetValue(out _) ? Results.NoContent() : result.ToHttp(v => v);
            })
            .ReturnsNoContent();

        // Read now, in the request: the bank sees the person's own browser (Psu-Ip-Address, Psu-User-Agent), as UseForwardedHeaders resolved it.
        api.MapPost("/sync", async (SyncBody body, HttpContext http, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new SyncMineCommand(body.ConnectionId, new PsuHeaders(
                    http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent is { Count: > 0 } agent ? agent.ToString() : null)), ct))
                .ToHttp(requested => requested, StatusCodes.Status202Accepted))
            .Returns<BankSyncRequested>(StatusCodes.Status202Accepted);

        // Spec §5.2: multipart/form-data, read by hand as a document upload (the app's own CSRF header, no form binding). confirm=false
        // previews, writing nothing; the browser sends the same file again with confirm=true.
        api.MapPost("/statements", async (HttpRequest request, IInvoker invoker, CancellationToken ct) =>
            {
                if (!request.HasFormContentType || !request.ContentType!.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                    return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Upload a multipart/form-data body.");
                var tooLarge = Result.Failure<StatementImportView>(new Failure(BankingCodes.FileTooLarge, "A statement file is at most 5 MiB.", null)).ToHttp(v => v);
                if (request.ContentLength > StatementReader.MaxBytes + FormSlack)
                    return tooLarge;
                if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                    limit.MaxRequestBodySize = StatementReader.MaxBytes + FormSlack;

                IFormCollection form;
                try
                {
                    // Held in memory up to the limit: the file is never stored, not even in a temporary file.
                    request.HttpContext.Features.Set<IFormFeature>(new FormFeature(request,
                        new FormOptions { MultipartBodyLengthLimit = StatementReader.MaxBytes, MemoryBufferThreshold = StatementReader.MaxBytes }));
                    form = await request.ReadFormAsync(ct);
                }
                catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    return tooLarge;
                }
                catch (InvalidDataException e) when (e.Message.StartsWith("Multipart body length limit", StringComparison.Ordinal))
                {
                    return tooLarge;
                }
                catch (InvalidDataException)
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The multipart body cannot be read.");
                }

                string? Text(string name) => form.TryGetValue(name, out var v) && v.Count > 0 ? v.ToString() : null;
                if (form.Files.GetFile("file") is not { } file)
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The file part is required.");
                if (file.Length > StatementReader.MaxBytes)
                    return tooLarge;
                Guid? envelopeId = null;
                if (Text("envelopeId") is { } rawEnvelope)
                {
                    if (!Guid.TryParse(rawEnvelope, out var parsed))
                        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The envelopeId is not a valid identifier.");
                    envelopeId = parsed;
                }
                bool? Flag(string name) => Text(name) is not { } raw ? false : bool.TryParse(raw, out var value) ? value : null;
                if (Flag("confirm") is not { } confirm || Flag("keepProbablyKnown") is not { } keep)
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "confirm and keepProbablyKnown are true or false.");

                await using var stream = file.OpenReadStream();
                var content = new byte[file.Length];
                await stream.ReadExactlyAsync(content, ct);
                return (await invoker.InvokeAsync(new ImportStatementCommand(content, envelopeId is { } id ? new ItemId(id) : null, confirm, keep), ct))
                    .ToHttp(view => view);
            })
            .Accepts<StatementForm>("multipart/form-data")
            .Returns<StatementImportView>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        var admin = routes.MapGroup("/api/v1/admin/banking").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        admin.MapGet("/runs", async (int? page, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetBankSyncRunsQuery(page), ct)).ToHttp(runs => runs))
            .Returns<BankSyncRunPage>();

        return routes;
    }

    /// <summary>Room for the form's other parts and boundaries on top of the file.</summary>
    private const long FormSlack = 64 * 1024;

    /// <summary>The import form, for the OpenAPI document only: the route reads the form itself.</summary>
    public sealed record StatementForm(IFormFile File, Guid? EnvelopeId, bool Confirm, bool KeepProbablyKnown);

    private static string Callback(IOptions<NidAuthOptions> auth) => auth.Value.PublicUrl.TrimEnd('/') + CallbackPath;

    public sealed record ConnectBody(string? AspspName);

    /// <summary>A field left out is left as it is; a key nobody reads is refused rather than silently dropped (400).</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record BankAccountBody(Guid? EnvelopeId, bool? ClearEnvelope, Guid? CardOfAccountId, bool? ClearCard, bool? AutoApplyBalance, bool? Ignored);

    /// <summary>A charge of <c>itemId</c> (a subscription, a loan, a property); <c>key</c> names one, else the nearest is taken.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record LinkBody(string? Module, Guid? ItemId, string? Key);

    /// <summary>Without <c>connectionId</c>, every connection of the person.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record SyncBody(Guid? ConnectionId);
}

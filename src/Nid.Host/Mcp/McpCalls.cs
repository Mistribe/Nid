using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Nid.Core.Application;
using Nid.Host.Api;
using Nid.Core.Contracts;
using Nid.Documents.Application;
using Nid.Documents.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Mcp;

/// <summary>
/// Spec 4B §2: the one way a tool runs a query. Same pipeline as REST (actor set by the call filter), same JSON as REST
/// (the host's HTTP JSON options), a refusal as a short tool error, a result above the limit refused whole. Spec 4D1 §5:
/// and the one way a write tool proposes or writes a document.
/// </summary>
public static partial class McpCalls
{
    public const string TooLarge = "Result too large: narrow the request.";
    public const string NotFound = "Not found.";

    private sealed record ConformeAnswer(string Outcome, JsonElement Expected, JsonElement Given);

    /// <param name="SourceLinked">Given a document only: whether it is now linked to the element, so the assistant can say so.</param>
    private sealed record AppliedAnswer(
        string Outcome, IReadOnlyList<Guid> AppliedRefs, IReadOnlyList<ProposalWarning> Skipped,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Summary,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? SourceLinked);

    private sealed record ProposedAnswer(
        string Outcome, bool Created, ProposalView? Proposal, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Summary,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? SourceLinked);

    /// <summary>The write tools' answers, with the HTTP options (NidJsonContexts).</summary>
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.Strict,
        Converters = [typeof(ItemIdJsonConverter), typeof(LocalDateJsonConverter)])]
    [JsonSerializable(typeof(object))]
    [JsonSerializable(typeof(ConformeAnswer))]
    [JsonSerializable(typeof(AppliedAnswer))]
    [JsonSerializable(typeof(ProposedAnswer))]
    [JsonSerializable(typeof(System.Text.Json.Nodes.JsonObject))]
    private sealed partial class AnswersJson : JsonSerializerContext;

    internal static JsonSerializerContext Answers => AnswersJson.Default;

    /// <summary>A JSON object built by hand (a payload, a schema), as an element.</summary>
    internal static JsonElement Element(System.Text.Json.Nodes.JsonObject node) => JsonSerializer.SerializeToElement(node, AnswersJson.Default.JsonObject);

    public static Task<CallToolResult> RunAsync<T>(IServiceProvider services, Func<IQuery<T>> build, CancellationToken ct)
        where T : notnull
        => RunAsync<T, T>(services, build, view => view, ct);

    public static async Task<CallToolResult> RunAsync<T, TOut>(
        IServiceProvider services, Func<IQuery<T>> build, Func<T, TOut> map, CancellationToken ct)
        where T : notnull
    {
        IQuery<T> request;
        try
        {
            request = build();
        }
        catch (BadHttpRequestException unreadable)
        {
            return Error(unreadable.Message);
        }

        var result = await services.GetRequiredService<IInvoker>().InvokeAsync(request, ct);
        if (result.TryGetFailure(out var failure))
            return ReadRefusal(failure!);

        result.TryGetValue(out var view);
        return Answer(services, map(view!));
    }

    /// <summary>A read refused: not found and unauthorized stay « Not found. », any other refusal names its code.</summary>
    internal static CallToolResult ReadRefusal(Failure failure)
        => Error(failure is NotFoundFailure or UnauthorizedFailure ? NotFound : $"Refused: {failure.Code}.");

    /// <summary>Tranche 3 design §4.1: the role a proposal's source document takes on its element, by kind.</summary>
    private static readonly Dictionary<string, string> SourceRoles = new(StringComparer.Ordinal)
    {
        ["investments.record_statement"] = DocumentRoles.Statement,
        ["investments.record_holdings_snapshot"] = DocumentRoles.Statement,
        ["income.change_pay"] = DocumentRoles.Payslip,
        ["realestate.import_amortization"] = DocumentRoles.AmortizationTable,
        ["subscriptions.change_price"] = DocumentRoles.Invoice,
    };

    /// <summary>
    /// Spec 4D1 §5: a document proposed (the default) or written directly (`apply`). The scope is checked first, so a
    /// missing one is named rather than answered « Not found. »; a refusal names its code and each refused field.
    /// Tranche 3 design §4.1: a <paramref name="documentId"/> the person cannot read answers « Not found. » before anything is
    /// written; one they read is linked to the element as the person, then carried by the proposal or the direct write.
    /// </summary>
    public static async Task<CallToolResult> ProposeAsync(
        IServiceProvider services, string kind, Guid? target, JsonElement body, string? sourceLabel, bool apply, Guid? documentId, CancellationToken ct)
    {
        if (services.GetRequiredService<IActorAccessor>().Current?.Has(apply ? ScopeNames.WriteDirect : ScopeNames.Write) != true)
            return Error(apply
                ? $"Refused: applying directly needs the '{ScopeNames.WriteDirect}' scope; call again without apply to propose."
                : $"Refused: proposing needs the '{ScopeNames.Write}' scope.");

        var invoker = services.GetRequiredService<IInvoker>();
        if (documentId is { } document)
        {
            // Review Focus 1: missing and unreadable alike, whatever the refusal.
            if (!(await invoker.InvokeAsync(new GetDocumentQuery(document), ct)).TryGetValue(out _))
                return Error(NotFound);
        }

        // The source follows the element once something was written or proposed (nothing for a refusal or a « conforme »), by the
        // UI's own link path and rights. Already linked (kept as it is) or refused, the proposal goes on without it, and its
        // views hide the document from whoever cannot read it. Null without a document; else whether the element now has it.
        async Task<bool?> LinkSourceAsync()
        {
            if (documentId is not { } document)
                return null;
            if (target is not { } item)
                return false;
            var linked = await invoker.InvokeAsync(new LinkDocumentCommand(document, new DocumentTarget(TargetKinds.CoreItem, item),
                SourceRoles.GetValueOrDefault(kind, DocumentRoles.Other), ProposalSource: true), ct);
            return linked.TryGetFailure(out var failure) ? failure!.Code == DocumentsCodes.AlreadyLinked : true;
        }

        if (apply)
        {
            var written = await invoker.InvokeAsync(new ApplyDirectCommand(kind, Item(target), body, documentId), ct);
            if (!written.TryGetValue(out var write))
                return Refusal(written.TryGetFailure(out var refused) ? refused! : throw new InvalidOperationException());
            if (write!.Conforme is { } same)
                return Answer(services, Conforme(same), limited: false);
            return Answer(services, Applied(write, await LinkSourceAsync()), limited: false);
        }

        var submitted = await invoker.InvokeAsync(new SubmitProposalCommand(kind, Item(target), body, sourceLabel, documentId), ct);
        if (!submitted.TryGetValue(out var outcome))
            return Refusal(submitted.TryGetFailure(out var rejected) ? rejected! : throw new InvalidOperationException());
        if (outcome!.Proposal is null)
            return Answer(services, Conforme(outcome.Conforme!.Value), limited: false);
        var sourceLinked = await LinkSourceAsync();
        var shown = (await ProposalEndpoints.HideDocumentsAsync(invoker, [outcome.Proposal], ct))[0];
        return Answer(services, new ProposedAnswer("proposed", outcome.Created, shown, outcome.Summary, sourceLinked), limited: false);
    }

    private static ItemId? Item(Guid? target) => target is { } id ? new ItemId(id) : null;

    private static object Conforme(JsonElement same)
        => new ConformeAnswer("conforme", same.GetProperty("expected"), same.GetProperty("given"));

    /// <summary>4D1's shapes; `summary` (spec 4D2 §2, the bank table's comparison) only for a kind that gives one.</summary>
    private static object Applied(DirectWrite w, bool? sourceLinked) => new AppliedAnswer("applied", w.AppliedRefs, w.Skipped, w.Summary, sourceLinked);

    /// <summary>Not found and unauthorized stay « Not found. »; any other refusal names its code, then each field (`payload.movements[3].amount: …`) or its message.</summary>
    private static CallToolResult Refusal(Failure failure)
    {
        if (failure is NotFoundFailure or UnauthorizedFailure)
            return Error(NotFound);
        var detail = FieldErrors.Of(failure) is { Count: > 0 } fields
            ? string.Join("; ", fields.Select(f => $"{f.Path}: {f.Message}"))
            : failure.Metadata is { } metadata && metadata.TryGetValue("field", out var field) && field is string path
                ? $"{path}: {failure.Message}"
                : failure.Message;
        return Error($"Refused: {failure.Code}. {detail}");
    }

    /// <summary>A read's result above <c>Mcp:MaxResultBytes</c> is refused whole; a write's answer (<paramref name="limited"/> false) only echoes its input, bounded per field, and is never refused after the write.</summary>
    internal static CallToolResult Answer<TOut>(IServiceProvider services, TOut value, bool limited = true)
    {
        var json = JsonSerializer.Serialize(value, services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions);
        var limit = services.GetRequiredService<IConfiguration>().GetValue("Mcp:MaxResultBytes", 102_400);
        return limited && System.Text.Encoding.UTF8.GetByteCount(json) > limit
            ? Error(TooLarge)
            : new CallToolResult { Content = [new TextContentBlock { Text = json }] };
    }

    public static CallToolResult Error(string message)
        => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}

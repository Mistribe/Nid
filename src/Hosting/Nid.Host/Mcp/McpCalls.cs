using System.Globalization;
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
using UnambitiousFx.Synapse.ModelContextProtocol;

namespace Nid.Host.Mcp;

/// <summary>
/// Spec 4B §2: the one way a tool runs a query, through Synapse's <see cref="IMcpInvoker"/>. Same pipeline as REST (actor set
/// by the call filter), same JSON as REST (the host's HTTP JSON options), every refusal as a tool error from Synapse's
/// <see cref="IMcpFailurePolicy"/> (<c>code: message</c>, a missing and an unreadable item alike), a read's answer above the
/// limit refused whole. Spec 4D1 §5: and the one way a write tool proposes or writes a document.
/// </summary>
public static partial class McpCalls
{
    /// <summary>A write tool asked for a scope the token does not hold.</summary>
    public const string ScopeRequired = "platform.scope_required";

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

    public static async Task<CallToolResult> RunAsync<T>(IServiceProvider services, Func<IQuery<T>> build, CancellationToken ct)
        where T : notnull
    {
        IQuery<T> request;
        try
        {
            request = build();
        }
        catch (BadHttpRequestException unreadable)
        {
            return Refuse(services, new Failure(FieldErrors.ShapeCode, unreadable.Message, null));
        }

        return await services.GetRequiredService<IMcpInvoker>().InvokeAsync(request, ct);
    }

    public static Task<CallToolResult> RunAsync<T, TOut>(
        IServiceProvider services, Func<IQuery<T>> build, Func<T, TOut> map, CancellationToken ct)
        where T : notnull
        => RunAsync<T, TOut>(services, build, view => Task.FromResult(map(view)), ct);

    public static async Task<CallToolResult> RunAsync<T, TOut>(
        IServiceProvider services, Func<IQuery<T>> build, Func<T, Task<TOut>> map, CancellationToken ct)
        where T : notnull
    {
        IQuery<T> request;
        try
        {
            request = build();
        }
        catch (BadHttpRequestException unreadable)
        {
            return Refuse(services, new Failure(FieldErrors.ShapeCode, unreadable.Message, null));
        }

        var result = await services.GetRequiredService<IInvoker>().InvokeAsync(request, ct);
        if (result.TryGetFailure(out var failure))
            return Refuse(services, failure!);

        result.TryGetValue(out var view);
        return Answer(services, await map(view!));
    }

    /// <summary>
    /// A refusal as Synapse's tool error: <c>NOT_FOUND</c> for a missing or unreadable item (and any <see cref="UnauthorizedFailure"/>),
    /// else <c>code: message</c>; a rule's refusal that names its fields lists them, one per line (<see cref="ProblemMapping.Located"/>).
    /// </summary>
    internal static CallToolResult Refuse(IServiceProvider services, Failure failure)
        => services.GetRequiredService<IMcpFailurePolicy>().ToToolError(ProblemMapping.Located(failure));

    /// <summary>An argument the tool could not read, as the field error <c>request.invalid</c> names.</summary>
    internal static CallToolResult Invalid(IServiceProvider services, string field, string message)
        => Refuse(services, FieldErrors.Shape([new FieldError(field, FieldErrors.ShapeCode, message)]));

    /// <summary>A write tool's scope check, before anything runs.</summary>
    internal static CallToolResult MissingScope(IServiceProvider services, string message)
        => Refuse(services, new Failure(ScopeRequired, message, null));

    /// <summary>Tranche 3 design §4.1: the role a proposal's source document takes on its element, by kind.</summary>
    private static readonly Dictionary<string, string> SourceRoles = new(StringComparer.Ordinal)
    {
        ["investments.record_statement"] = DocumentRoles.Statement,
        ["investments.record_holdings_snapshot"] = DocumentRoles.Statement,
        ["investments.record_breakdown"] = DocumentRoles.Other, // a factsheet is none of the listed roles
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
            return MissingScope(services, apply
                ? $"Applying directly needs the '{ScopeNames.WriteDirect}' scope; call again without apply to propose."
                : $"Proposing needs the '{ScopeNames.Write}' scope.");

        var invoker = services.GetRequiredService<IInvoker>();
        if (documentId is { } document)
        {
            // Review Focus 1: missing and unreadable alike, whatever the refusal.
            if (!(await invoker.InvokeAsync(new GetDocumentQuery(document), ct)).TryGetValue(out _))
                return Refuse(services, new NotFoundFailure("document", document.ToString()));
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
                return Refuse(services, written.TryGetFailure(out var refused) ? refused! : throw new InvalidOperationException());
            if (write!.Conforme is { } same)
                return Answer(services, Conforme(same), limited: false);
            return Answer(services, Applied(write, await LinkSourceAsync()), limited: false);
        }

        var submitted = await invoker.InvokeAsync(new SubmitProposalCommand(kind, Item(target), body, sourceLabel, documentId), ct);
        if (!submitted.TryGetValue(out var outcome))
            return Refuse(services, submitted.TryGetFailure(out var rejected) ? rejected! : throw new InvalidOperationException());
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

    /// <summary>
    /// A hand-built answer (a transformed view, a write's outcome), as <see cref="IMcpInvoker"/> answers a request: the host's
    /// HTTP JSON, and for a read (<paramref name="limited"/>) the answer refused whole above <see cref="SynapseMcpOptions.MaxAnswerLength"/>
    /// with Synapse's <c>ANSWER_TOO_LARGE</c>. A write's answer only echoes its input, bounded per field, and is never refused after the write.
    /// </summary>
    internal static CallToolResult Answer<TOut>(IServiceProvider services, TOut value, bool limited = true)
    {
        var options = services.GetRequiredService<IOptions<SynapseMcpOptions>>().Value;
        var json = JsonSerializer.Serialize(value, options.SerializerOptions!.GetTypeInfo(typeof(TOut)));
        return limited && options.MaxAnswerLength is { } max && json.Length > max
            ? Refuse(services, new Failure(SynapseMcpOptions.AnswerTooLargeCode, string.Create(CultureInfo.InvariantCulture,
                $"The answer is {json.Length} characters, above the {max} limit: narrow the request (a shorter period, a filter, a page)."), null))
            : new CallToolResult { Content = [new TextContentBlock { Text = json }] };
    }
}

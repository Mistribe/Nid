using System.Reflection;
using System.Reflection.Emit;
using Nid.Banking.Application;
using Nid.Banking.Application.Files;
using Nid.Banking.Application.Sync;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Documents.Application;
using Nid.Documents.Contracts;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.IntegrationTests.LeakBench;

/// <summary>
/// #516, architecture.md §5.4: access to a bank account, a bank line, a document or a proposal is checked by a pipeline behavior that
/// reads the request's marker, not by hand in each handler. Reflection only, no database: a handler that checks again (or a request that
/// forgets its marker and so its check) fails here.
/// </summary>
public class ResourceMarkerTests
{
    private static readonly Type ProposalAccess = typeof(Nid.Core.Application.ProposalView).Assembly.GetType("Nid.Core.Application.ProposalAccess", true)!;

    /// <summary>The checks the behaviors make, which a handler no longer calls.</summary>
    private static readonly MethodInfo[] BehaviorChecks =
    [
        typeof(BankingAccess).GetMethod(nameof(BankingAccess.EnabledAsync))!,
        typeof(BankingAccess).GetMethod(nameof(BankingAccess.AccountAsync))!,
        typeof(BankLines).GetMethod(nameof(BankLines.FindAsync))!,
        typeof(DocumentAccess).GetMethod(nameof(DocumentAccess.ReadAsync))!,
        typeof(DocumentAccess).GetMethod(nameof(DocumentAccess.EditableAsync))!,
        ProposalAccess.GetMethod("LoadAsync")!,
    ];

    /// <summary>The handlers that still call one, each for a resource its request does not name: the reason is the entry's.</summary>
    private static readonly Dictionary<(string Handler, string Check), string> Kept = new()
    {
        [(nameof(UpdateBankAccountHandler), nameof(BankingAccess.AccountAsync))] = "the account a card is attached to, after the owner check",
        [("UnlinkTransactionHandler", nameof(BankingAccess.AccountAsync))] = "the link's counterpart account (D19), not the line's",
        [(nameof(ImportStatementHandler), nameof(BankingAccess.AccountAsync))] = "the account the file names, found by its IBAN",
        [(nameof(SyncAllHandler), nameof(BankingAccess.EnabledAsync))] = "the nightly job runs its retention with banking off too",
        [(nameof(UploadDocumentHandler), nameof(DocumentAccess.ReadAsync))] = "a copy found by its hash or its source reference",
        // Tax assessments spec §5: the declarants are checked first (« tax assessment » not found); moves with #512's Nid.Tax.
        [(nameof(LinkTaxNoticeHandler), nameof(DocumentAccess.ReadAsync))] = "the declarants come first; follows #512",
        [(nameof(PreviewTaxNoticeLinkHandler), nameof(DocumentAccess.ReadAsync))] = "the declarants come first; follows #512",
    };

    [Fact]
    public void No_handler_checks_what_its_resource_behavior_checks()
    {
        var handlers = RequestCatalogue.ModuleAssemblies.SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                        && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)))
            .ToList();
        Assert.NotEmpty(handlers);

        var calls = handlers.SelectMany(h => CalledBy(h).Where(m => BehaviorChecks.Contains(m)).Distinct().Select(m => (Handler: h.Name, Check: m.Name))).ToList();
        var unexplained = calls.Where(c => !Kept.ContainsKey(c)).Select(c => $"{c.Handler} calls {c.Check}").ToList();
        Assert.True(unexplained.Count == 0,
            "These handlers check by hand what a resource behavior checks (#516): mark the request instead (IRequiresBanking, IBankAccountScoped, "
            + "IBankLineScoped, IReadsDocument, IEditsDocument, IProposalScoped) and read what the behavior hands on (CheckedResources):\n  "
            + string.Join("\n  ", unexplained));
        // An entry nobody needs any more goes too, so the list stays the list of exceptions.
        Assert.Empty(Kept.Keys.Except(calls));
    }

    [Fact]
    public void The_call_walk_sees_a_check_inside_an_async_handler() =>
        Assert.Contains(typeof(DocumentAccess).GetMethod(nameof(DocumentAccess.ReadAsync))!, CalledBy(typeof(UploadDocumentHandler)));

    /// <summary>
    /// Every Banking request asks for banking on, but those that make sense with it off: the status, the person's own application, the
    /// administrator's diagnostics and journal, the household's categories and budgets (no account, no line). A new one has to pick a side.
    /// </summary>
    [Fact]
    public void Every_banking_request_asks_for_banking_on_or_is_named_here()
    {
        Type[] offToo =
        [
            typeof(GetBankingStatusQuery), typeof(GetMyApplicationQuery), typeof(SaveMyApplicationCommand), typeof(DeleteMyApplicationCommand),
            typeof(TestMyApplicationCommand), typeof(SyncAllCommand), typeof(GetBankSyncRunsQuery), typeof(GetBankingDiagnosticsQuery),
            typeof(TestBankingConnectionCommand), typeof(TransferInstallationApplicationCommand), typeof(ListBankCategoriesQuery),
            typeof(Nid.Banking.Application.Spending.ListBudgetsQuery),
        ];
        var banking = RequestCatalogue.Discover().Where(t => t.Assembly == typeof(BankingAccess).Assembly).ToList();
        Assert.NotEmpty(banking);
        Assert.All(banking.Except(offToo), t => Assert.True(typeof(IRequiresBanking).IsAssignableFrom(t), $"{t.Name} does not ask for banking on"));
        Assert.All(offToo, t => Assert.False(typeof(IRequiresBanking).IsAssignableFrom(t), $"{t.Name} is named as running with banking off"));
        Assert.True(typeof(IRequiresBanking).IsAssignableFrom(typeof(Nid.Host.Composition.CreateBankAccountEnvelopeCommand)));
    }

    /// <summary>The resource axis of the bench reaches every request carrying a bank account, bank line or proposal marker.</summary>
    [Fact]
    public void Every_resource_scoped_request_is_on_the_resource_axis()
    {
        var scoped = RequestCatalogue.Discover()
            .Where(t => typeof(IBankAccountScoped).IsAssignableFrom(t) || typeof(IBankLineScoped).IsAssignableFrom(t) || typeof(IProposalScoped).IsAssignableFrom(t))
            .ToList();
        Assert.NotEmpty(scoped);
        var missing = scoped.Where(t => !RequestCatalogue.Entries.Any(e => e.RequestType == t && e.BuildOn is not null)).Select(t => t.FullName).ToList();
        Assert.True(missing.Count == 0, "These requests carry a resource marker but have no BuildOn in RequestCatalogue:\n  " + string.Join("\n  ", missing));
    }

    /// <summary>Every method a type's code calls, its compiler-made state machines and closures included.</summary>
    private static IEnumerable<MethodBase> CalledBy(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var called = new List<MethodBase>();
        foreach (var t in Nested(type))
        foreach (var method in t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)))
        {
            if (method.GetMethodBody()?.GetILAsByteArray() is not { } il)
                continue;
            for (var i = 0; i < il.Length;)
            {
                var op = il[i] == 0xFE ? TwoByte[il[i + 1]] : OneByte[il[i]];
                i += op.Size;
                if (op.OperandType == OperandType.InlineMethod)
                {
                    try
                    {
                        called.Add(method.Module.ResolveMethod(BitConverter.ToInt32(il, i),
                            t.IsGenericType ? t.GetGenericArguments() : null, method.IsGenericMethod ? method.GetGenericArguments() : null)!);
                    }
                    catch (ArgumentException)
                    {
                        // A method of a generic context this walk does not rebuild: none of the checks is one.
                    }
                }
                i += op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, i)),
                    _ => 4,
                };
            }
        }
        return called;
    }

    private static IEnumerable<Type> Nested(Type type)
        => type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(Nested).Prepend(type);

    private static readonly Dictionary<byte, OpCode> OneByte = Codes().Where(o => o.Size == 1).ToDictionary(o => (byte)o.Value);

    private static readonly Dictionary<byte, OpCode> TwoByte = Codes().Where(o => o.Size == 2).ToDictionary(o => (byte)(o.Value & 0xFF));

    private static IEnumerable<OpCode> Codes()
        => typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (OpCode)f.GetValue(null)!);
}

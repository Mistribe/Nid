using System.Text.Json;
using System.Text.Json.Serialization;
using Nid.Banking.Application;

namespace Nid.Host.Api.Json;

/// <summary>The Banking module's request and response bodies, for NidJsonContexts.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.Strict,
    Converters = [typeof(ItemIdJsonConverter), typeof(LocalDateJsonConverter), typeof(InstantJsonConverter)])]
[JsonSerializable(typeof(BankingStatusView))]
[JsonSerializable(typeof(IReadOnlyList<AspspView>))]
[JsonSerializable(typeof(ConnectionStarted))]
[JsonSerializable(typeof(IReadOnlyList<ConnectionView>))]
[JsonSerializable(typeof(IReadOnlyList<BankAccountView>))]
[JsonSerializable(typeof(BankTransactionPage))]
[JsonSerializable(typeof(Mcp.BankingTools.UncategorisedLines))]
[JsonSerializable(typeof(BankTransactionView))]
[JsonSerializable(typeof(IReadOnlyList<ExpectedChargeView>))]
[JsonSerializable(typeof(IReadOnlyList<BankRuleView>))]
[JsonSerializable(typeof(BankingEndpoints.LinkBody))]
[JsonSerializable(typeof(BankingEndpoints.LineCategoryBody))]
[JsonSerializable(typeof(BankCategoryView))]
[JsonSerializable(typeof(IReadOnlyList<BankCategoryView>))]
[JsonSerializable(typeof(BankingEndpoints.CategoryBody))]
[JsonSerializable(typeof(BankingEndpoints.CategoryPatchBody))]
[JsonSerializable(typeof(BankingEndpoints.CategoryMergeBody))]
[JsonSerializable(typeof(CategoryRuleView))]
[JsonSerializable(typeof(IReadOnlyList<CategoryRuleView>))]
[JsonSerializable(typeof(CategoryRulePreview))]
[JsonSerializable(typeof(BankingEndpoints.CategoryRuleBody))]
[JsonSerializable(typeof(BankingEndpoints.CategoryRuleApplied))]
[JsonSerializable(typeof(BankingEndpoints.ConnectBody))]
[JsonSerializable(typeof(BankingEndpoints.BankAccountBody))]
[JsonSerializable(typeof(BankingEndpoints.BankAccountEnvelopeBody))]
[JsonSerializable(typeof(BankingEndpoints.SyncBody))]
[JsonSerializable(typeof(Nid.Banking.Application.Sync.BankSyncRequested))]
[JsonSerializable(typeof(BankSyncRunPage))]
[JsonSerializable(typeof(BankingDiagnosticsView))]
[JsonSerializable(typeof(BankingConnectionTestView))]
[JsonSerializable(typeof(Nid.Banking.Application.Files.StatementImportView))]
internal sealed partial class BankingJsonContext : JsonSerializerContext;

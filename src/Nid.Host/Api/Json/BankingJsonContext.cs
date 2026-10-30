using System.Text.Json;
using System.Text.Json.Serialization;
using Nid.Banking.Application;

namespace Nid.Host.Api.Json;

/// <summary>The Banking module's request and response bodies, for NidJsonContexts.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.Strict,
    Converters = [typeof(ItemIdJsonConverter), typeof(LocalDateJsonConverter)])]
[JsonSerializable(typeof(BankingStatusView))]
[JsonSerializable(typeof(IReadOnlyList<AspspView>))]
[JsonSerializable(typeof(ConnectionStarted))]
[JsonSerializable(typeof(IReadOnlyList<ConnectionView>))]
[JsonSerializable(typeof(IReadOnlyList<BankAccountView>))]
[JsonSerializable(typeof(BankTransactionPage))]
[JsonSerializable(typeof(BankingEndpoints.ConnectBody))]
[JsonSerializable(typeof(BankingEndpoints.BankAccountBody))]
[JsonSerializable(typeof(BankingEndpoints.SyncBody))]
[JsonSerializable(typeof(Nid.Banking.Application.Sync.BankSyncRequested))]
[JsonSerializable(typeof(IReadOnlyList<BankSyncRunView>))]
[JsonSerializable(typeof(Nid.Banking.Application.Files.StatementImportView))]
internal sealed partial class BankingJsonContext : JsonSerializerContext;

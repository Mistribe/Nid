using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Nid.Host.Api.Json;

/// <summary>
/// The source-generated JSON metadata of the API's bodies, one context per module, so that a NativeAOT build has every
/// contract without reflection. They go first in the chain; reflection stays behind them in production, for a type none
/// declares. The integration tests run without that fallback (NidApp), and JsonContractTests checks every endpoint.
/// </summary>
public static class NidJsonContexts
{
    private static readonly IJsonTypeInfoResolver[] All =
    [
        CoreJsonContext.Default, SubscriptionsJsonContext.Default, InvestmentsJsonContext.Default, IncomeJsonContext.Default,
        GoalsJsonContext.Default, BankingJsonContext.Default, DocumentsJsonContext.Default, RealEstateJsonContext.Default, PlatformJsonContext.Default, Mcp.McpCalls.Answers,
    ];

    public static void AddTo(JsonSerializerOptions options)
    {
        for (var i = 0; i < All.Length; i++)
            options.TypeInfoResolverChain.Insert(i, All[i]);
    }
}

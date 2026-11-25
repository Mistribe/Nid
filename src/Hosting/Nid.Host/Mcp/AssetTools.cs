using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nid.Assets.Application;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Host.Mcp;

[McpServerToolType]
public sealed class AssetTools
{
    [McpServerTool(Name = "list_assets", ReadOnly = true)]
    [Description("The other assets (vehicles, precious metals, art, collectibles) with their latest value, one page of them. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> ListAssets(IServiceProvider services,
        [Description("'household' (default) or 'me'.")] string? scope = null,
        [Description("'vehicle', 'precious_metal', 'art', 'collectible' or 'other'.")] string? kind = null,
        [Description("A person's id (list_persons): only the assets they own.")] Guid? owner = null,
        [Description("Archived assets instead of the active ones.")] bool archived = false,
        [Description("Search in the names.")] string? q = null, [Description("Page, from 1.")] int page = 1, CancellationToken ct = default)
        => McpCalls.RunAsync(services, r => new ListAssetsQuery(r.Scope(scope),
            new AssetFilter(string.IsNullOrEmpty(kind) ? null : kind, owner is { } o ? new PersonId(o) : null, archived, q, page)), ct);

    [McpServerTool(Name = "list_asset_costs", ReadOnly = true)]
    [Description("A vehicle's running costs (service, repair, tyres, other) of one year, newest first, with the year's total. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> ListAssetCosts(IServiceProvider services,
        [Description("The asset's id.")] Guid id,
        [Description("The year; the current one when left out.")] int? year = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new ListAssetCostsQuery(new ItemId(id), year), ct);

    [McpServerTool(Name = "get_asset", ReadOnly = true)]
    [Description("One asset: its details, purchase and valuations; for a vehicle its energy, mileage and the value estimated from a yearly depreciation rate (valueEstimated, valueBase), whether it has costs (hasCosts), the subscriptions linked to it and the loans financing it, with the net value after their balances (netValue; fxMissing when a loan has no exchange rate and is not deducted). " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetAsset(IServiceProvider services, [Description("The asset's id.")] Guid id, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetAssetQuery(new ItemId(id)), ct);
}

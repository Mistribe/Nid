using Nid.Core.Contracts;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;

namespace Nid.TestSupport;

/// <summary>Custom providers (design §3.5): created and listed through the commands, as an adult would.</summary>
public static class ProviderApi
{
    public static async Task<string> CreateCustomAsync(NidApp app, PersonId actor, string name)
    {
        var (created, failure) = await app.InvokeAsync(actor, new CreateProviderCommand(name, null, null, [CatalogLimits.OtherLabel]));
        Assert.Null(failure);
        return ((ProviderView)created!).Key;
    }

    public static async Task<List<string>> VisibleAsync(NidApp app, PersonId actor, string? q = null)
    {
        var (list, failure) = await app.InvokeAsync(actor, new ListProvidersQuery(q));
        Assert.Null(failure);
        return ((IReadOnlyList<ProviderView>)list!).Select(p => p.Key).ToList();
    }
}

namespace Nid.Host.IntegrationTests.Import;

/// <summary>
/// The three worked examples of docs/subscription-data-model.md as one nid.data file: Spotify with a price
/// change and a gift card, the NordVPN term with bonus months, the game bundle. One amount is written as a JSON
/// number on purpose (import-format.md §2, "tolerant on form").
/// </summary>
public static class GoldenFile
{
    public const string Persons = """
        [{"ref":"adult-1","kind":"adult","display_name":"Adulte 1"},
         {"ref":"adult-2","kind":"adult","display_name":"Adulte 2"}]
        """;

    /// <summary>The three providers as custom ones: a test database holds no catalog unless a test syncs one.</summary>
    public const string Providers = """
        [{"key":"spotify","name":"Spotify","website":"https://www.spotify.com","label_keys":["other"]},
         {"key":"nordvpn","name":"NordVPN","label_keys":["other"]},
         {"key":"wow","name":"World of Warcraft","label_keys":["other"]}]
        """;

    public const string Subscriptions = """
        [{"id":"sub-spotify","provider_key":"spotify","name":"Spotify Premium Duo",
          "ownership":[{"person":"adult-1","share_bp":10000}],"service_users":["adult-1"],"auto_renew":true},
         {"id":"sub-nordvpn","provider_key":"nordvpn","name":"NordVPN",
          "ownership":[{"person":"adult-1","share_bp":10000}],"auto_renew":true},
         {"id":"sub-wow","provider_key":"wow","name":"World of Warcraft",
          "ownership":[{"person":"adult-1","share_bp":10000}],"auto_renew":true}]
        """;

    public const string Phases = """
        [{"id":"ph-spotify-1","subscription_id":"sub-spotify","starts_on":"2024-06-28","ends_on":"2025-06-28",
          "price":{"amount":"15.17","currency":"EUR"},"every":{"count":1,"unit":"month"},"payer":{"type":"household"}},
         {"id":"ph-spotify-2","subscription_id":"sub-spotify","starts_on":"2025-06-28","ends_on":null,
          "price":{"amount":17.20,"currency":"EUR"},"every":{"count":1,"unit":"month"},"payer":{"type":"household"}}]
        """;

    public const string Purchases = """
        [{"id":"pu-gift","date":"2025-10-10","label":"Carte cadeau 3 mois","kind":"gift",
          "price":{"amount":"0.00","currency":"EUR"},"payer":{"type":"external"},
          "grants":[{"subscription_id":"sub-spotify","duration":{"count":3,"unit":"month"},"source":"gift"}]},
         {"id":"pu-nordvpn","date":"2025-06-22","label":"NordVPN 2 ans","kind":"prepaid_term",
          "price":{"amount":"179.86","currency":"EUR"},"payer":{"type":"person","person":"adult-1"},
          "grants":[{"subscription_id":"sub-nordvpn","duration":{"count":24,"unit":"month"},"source":"paid","starts_on":"2025-06-22"},
                    {"subscription_id":"sub-nordvpn","duration":{"count":6,"unit":"month"},"source":"bonus"}]},
         {"id":"pu-wow","date":"2024-11-18","label":"Extension","kind":"bundle",
          "price":{"amount":"88.99","currency":"EUR"},"payer":{"type":"person","person":"adult-1"},"allocated_amount":"0",
          "grants":[{"subscription_id":"sub-wow","duration":{"count":1,"unit":"month"},"source":"included"}]}]
        """;

    public static Dictionary<string, string> Sections() => new()
    {
        ["providers"] = Providers,
        ["persons"] = Persons,
        ["subscriptions"] = Subscriptions,
        ["billing_phases"] = Phases,
        ["purchases"] = Purchases,
    };

    public static Dictionary<string, string> With(string section, string entities)
    {
        var sections = Sections();
        sections[section] = entities;
        return sections;
    }
}

namespace Nid.Subscriptions.IntegrationTests.Import;

/// <summary>
/// Spec 3F1's household as file sections: a home co-owned by both adults and a studio let by adult-1, their planned costs and
/// tenancies. Each entity is one JSON object, so a test changes one member with <c>Replace</c>.
/// </summary>
internal static class EstateFile
{
    public const string Home = """
        {"id":"prop-home","name":"Maison","property_type":"primary_residence","currency":"EUR","purchase_date":"2020-06-15",
         "purchase_price":{"amount":"250000.00","currency":"EUR"},"notary_fees":{"amount":"19000.00","currency":"EUR"},
         "ownership":[{"person":"adult-1","share_bp":5000},{"person":"adult-2","share_bp":5000}],"visibility":"shared"}
        """;

    public const string Flat = """
        {"id":"prop-flat","name":"Studio","property_type":"rental","currency":"EUR","purchase_date":"2021-03-01",
         "purchase_price":{"amount":"120000.00","currency":"EUR"},"surface_m2":"24.5","location_label":"Lyon 7e",
         "rental_starts_on":"2021-04-01","ownership":[{"person":"adult-1","share_bp":10000}]}
        """;

    public const string Tax = """
        {"id":"cost-tax","property":"prop-home","category":"property_tax","amount":{"amount":"1200.00","currency":"EUR"},
         "every":{"count":1,"unit":"year"},"starts_on":"2020-10-15"}
        """;

    public const string Condo = """
        {"id":"cost-condo","property":"prop-flat","category":"condo_fees","amount":{"amount":"150.00","currency":"EUR"},
         "every":{"count":3,"unit":"month"},"starts_on":"2021-04-01","note":"syndic"}
        """;

    public const string Lease1 = """
        {"id":"ten-1","property":"prop-flat","label":"T1 meublé","starts_on":"2021-04-01","ends_on":"2024-04-01",
         "rent_monthly":{"amount":"650.00","currency":"EUR"},"charges_monthly":{"amount":"50.00","currency":"EUR"},
         "deposit":{"amount":"650.00","currency":"EUR"}}
        """;

    public const string Lease2 = """
        {"id":"ten-2","property":"prop-flat","label":"T1 meublé","starts_on":"2024-04-01",
         "rent_monthly":{"amount":"700.00","currency":"EUR"},"charges_monthly":{"amount":"50.00","currency":"EUR"}}
        """;

    public static string List(params string[] entities) => "[" + string.Join(",", entities) + "]";

    public static Dictionary<string, string> Properties(string? properties = null, string? costs = null, string? tenancies = null)
        => new()
        {
            ["persons"] = GoldenFile.Persons,
            ["properties"] = properties ?? List(Home, Flat),
            ["property_recurring_costs"] = costs ?? List(Tax, Condo),
            ["tenancies"] = tenancies ?? List(Lease1, Lease2),
        };
}

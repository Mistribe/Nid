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

    public const string MainLoan = """
        {"id":"loan-main","name":"Prêt principal","lender":"Banque","currency":"EUR","principal":{"amount":"200000.00","currency":"EUR"},
         "disbursed_on":"2020-10-15","first_payment_date":"2020-11-15","total_payments":240,"property":"prop-home",
         "ownership":[{"person":"adult-1","share_bp":5000},{"person":"adult-2","share_bp":5000}],"visibility":"shared"}
        """;

    /// <summary>A zero-rate loan on the same home: five years deferred, then repaid without interest.</summary>
    public const string ZeroLoan = """
        {"id":"loan-ptz","name":"Prêt à taux zéro","currency":"EUR","principal":{"amount":"40000.00","currency":"EUR"},
         "disbursed_on":"2020-10-15","first_payment_date":"2020-11-15","total_payments":240,"property":"prop-home",
         "ownership":[{"person":"adult-1","share_bp":5000},{"person":"adult-2","share_bp":5000}]}
        """;

    public const string MainRate = """{"id":"lp-1","loan":"loan-main","seq":1,"mode":"annuity","annual_rate":"0.035","rate_type":"fixed"}""";

    public const string ZeroDeferred = """{"id":"lp-2","loan":"loan-ptz","seq":1,"mode":"deferred_total","annual_rate":"0","rate_type":"fixed","payments":60}""";

    /// <summary>Its rate as a JSON number: tolerated on the form (import-format.md §2).</summary>
    public const string ZeroAnnuity = """{"id":"lp-3","loan":"loan-ptz","seq":2,"mode":"annuity","annual_rate":0,"rate_type":"fixed"}""";

    public const string Insurance = """{"id":"li-1","loan":"loan-main","mode":"rate_on_initial","annual_rate":"0.003","starts_on":"2020-11-15"}""";

    public const string FileFee = """{"id":"fee-file","loan":"loan-main","kind":"file","amount":{"amount":"1000.00","currency":"EUR"},"on_date":"2020-10-15"}""";

    public const string Repayment = """{"id":"ev-1","loan":"loan-main","on_date":"2023-03-15","amount":{"amount":"10000.00","currency":"EUR"},"effect":"reduce_term"}""";

    /// <summary>The main loan's first three payments as its bank writes them (loans.md §4): decimal strings in its currency.</summary>
    public const string Bank1 = """{"id":"lbs-1","loan":"loan-main","due_date":"2020-11-15","payment":"1159.92","interest":"583.33","principal":"576.59","crd":"199423.41"}""";

    public const string Bank2 = """{"id":"lbs-2","loan":"loan-main","due_date":"2020-12-15","payment":"1159.92","interest":"581.65","principal":"578.27","crd":"198845.14"}""";

    public const string Bank3 = """{"id":"lbs-3","loan":"loan-main","due_date":"2021-01-15","payment":"1159.92","interest":"579.96","principal":"579.96","crd":"198265.18"}""";

    /// <summary>The main loan with the bank as the reference.</summary>
    public static readonly string BankLoan = MainLoan.Replace("\"visibility\":\"shared\"", "\"visibility\":\"shared\",\"schedule_source\":\"bank\"");

    public static string List(params string[] entities) => "[" + string.Join(",", entities) + "]";

    public static Dictionary<string, string> Properties(string? properties = null, string? costs = null, string? tenancies = null)
        => new()
        {
            ["persons"] = GoldenFile.Persons,
            ["properties"] = properties ?? List(Home, Flat),
            ["property_recurring_costs"] = costs ?? List(Tax, Condo),
            ["tenancies"] = tenancies ?? List(Lease1, Lease2),
        };

    /// <summary>The properties and both loans; the phases are listed out of their seq order on purpose (Review Focus 4).</summary>
    public static Dictionary<string, string> Loans(
        string? loans = null, string? phases = null, string? insurances = null, string? fees = null, string? events = null, string? bank = null)
    {
        var file = Properties();
        file["loans"] = loans ?? List(MainLoan, ZeroLoan);
        file["loan_phases"] = phases ?? List(ZeroAnnuity, MainRate, ZeroDeferred);
        file["loan_insurances"] = insurances ?? List(Insurance);
        file["loan_fees"] = fees ?? List(FileFee);
        file["loan_events"] = events ?? List(Repayment);
        if (bank is not null)
            file["loan_bank_schedule"] = bank;
        return file;
    }
}

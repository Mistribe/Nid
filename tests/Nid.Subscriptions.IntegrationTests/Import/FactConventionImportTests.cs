using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.Import.EnvelopesImportTests;

namespace Nid.Subscriptions.IntegrationTests.Import;

/// <summary>3F1 spec §3: the Core's <c>flows</c> section consults the conventions modules declare, and only those.</summary>
public class FactConventionImportTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>A convention on envelopes, as a module would declare one: expenses of category « fees » only, a category required.</summary>
    private sealed class EnvelopeFees : IFactConventions
    {
        public IReadOnlyList<FactConvention> Conventions { get; } =
        [
            new("investments", "envelope", "envelopes", "stub.invalid_category", true,
                new Dictionary<FlowKind, IReadOnlyList<string>> { [FlowKind.Expense] = ["fees"] }),
        ];
    }

    private const string Flows = """
        [{"id":"f-ok","item":"env-pea","occurred_on":"2025-03-15","kind":"expense","amount":{"amount":"2","currency":"EUR"},"category":"fees"},
         {"id":"f-cat","item":"env-pea","occurred_on":"2025-03-16","kind":"expense","amount":{"amount":"2","currency":"EUR"},"category":"rent"},
         {"id":"f-kind","item":"env-pea","occurred_on":"2025-03-17","kind":"contribution","amount":{"amount":"2","currency":"EUR"}},
         {"id":"f-none","item":"env-pea","occurred_on":"2025-03-18","kind":"expense","amount":{"amount":"2","currency":"EUR"}}]
        """;

    private static List<(string Code, string Path)> ErrorsOf(SessionResponse session)
        => session.Report!.Issues.Where(i => i.Severity == "error").Select(i => (i.Code, i.Path)).ToList();

    [Fact]
    public async Task A_flow_outside_its_items_convention_is_refused_at_its_member()
    {
        await using var app = await NidApp.StartAsync(fixture, services => services.AddSingleton<IFactConventions>(new EnvelopeFees()));

        var validated = await ValidateAsync(new ImportClient(app), app, app.AdultA, FactsImportTests.Facts(valuations: "[]", flows: Flows));

        Assert.Equal("invalid", validated.State);
        Assert.Equal(
            [("stub.invalid_category", "flows[1].category"), ("stub.invalid_category", "flows[2].kind"), ("stub.invalid_category", "flows[3].category")],
            ErrorsOf(validated));
        Assert.Equal(new CountsResponse(1, 0, 0), validated.Report!.Sections["flows"]);
    }

    [Fact]
    public async Task Envelope_flows_keep_any_category_when_no_module_declares_a_convention_for_them()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var validated = await ValidateAsync(new ImportClient(app), app, app.AdultA, FactsImportTests.Facts(valuations: "[]", flows: Flows));

        Assert.Equal("validated", validated.State);
        Assert.Equal(new CountsResponse(4, 0, 0), validated.Report!.Sections["flows"]);
    }

    [Fact]
    public async Task The_host_declares_the_property_convention_of_README_section_4()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var convention = app.Services.GetServices<IFactConventions>().SelectMany(c => c.Conventions).Single(c => c.Section == "properties");

        Assert.Equal(("realestate", "property", PropertyCodes.InvalidCategory, true),
            (convention.Module, convention.Kind, convention.Code, convention.CategoryRequired));
        Assert.Equal(2, convention.Categories.Count);
        Assert.Equal(PropertyCategories.Expense, convention.Categories[FlowKind.Expense]);
        Assert.Equal(PropertyCategories.Income, convention.Categories[FlowKind.Income]);
    }
}

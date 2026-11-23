using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Tax.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Tax.IntegrationTests;

/// <summary>#512: what Core reads of the tax module through <see cref="IHouseholdTax"/>, where the tax page's own tests do not reach.</summary>
public class HouseholdTaxTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class FailingLines : ITaxContribution
    {
        public Task<Result<IReadOnlyList<TaxLine>>> TaxLinesAsync(PersonId reader, VisibilityScope scope, int year, LocalDate today, CancellationToken ct)
            => Task.FromResult(Result.Failure<IReadOnlyList<TaxLine>>(new Failure("test.unreadable", "A module cannot read its lines.", null)));
    }

    [Fact]
    public async Task A_rental_purchase_s_tax_fails_with_the_year_it_cannot_estimate()
    {
        await using var app = await NidApp.StartAsync(fixture, services => services.AddScoped<ITaxContribution, FailingLines>());
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);

        var increase = await scope.ServiceProvider.GetRequiredService<IHouseholdTax>().RentalPurchaseAsync(
            VisibilityScope.Household, new LocalDate(2026, 3, 1), new RentalPurchaseTax("real", 9_600m, 1_200m, 2_000m), TestContext.Current.CancellationToken);

        Assert.True(increase.TryGetFailure(out var failure));
        Assert.Equal("test.unreadable", failure!.Code);
    }

    [Fact]
    public async Task A_rental_purchase_under_the_real_regime_raises_the_tax_by_its_rents_less_its_charges()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
        var tax = scope.ServiceProvider.GetRequiredService<IHouseholdTax>();
        var today = new LocalDate(2026, 3, 1);

        var micro = await tax.RentalPurchaseAsync(VisibilityScope.Household, today, new RentalPurchaseTax("micro", 9_600m, 1_200m, 2_000m), TestContext.Current.CancellationToken);
        var real = await tax.RentalPurchaseAsync(VisibilityScope.Household, today, new RentalPurchaseTax("real", 9_600m, 1_200m, 2_000m), TestContext.Current.CancellationToken);

        Assert.True(micro.TryGetValue(out var underMicro) && underMicro > 0m);
        // The real regime deducts the charges and the loans' interest the micro-foncier's allowance stands for.
        Assert.True(real.TryGetValue(out var underReal) && underReal < underMicro);
    }
}

using Nid.Core.Contracts;

namespace Nid.Core.Tests;

/// <summary>#155: the market profiles, derived from the household's rates and volatilities.</summary>
public class ProjectionProfilesTests
{
    [Fact]
    public void The_defaults_are_balanced() => Assert.Equal(ProjectionProfiles.Balanced, ProjectionProfiles.Of(ProjectionDefaults.Assumptions));

    [Theory]
    [InlineData(ProjectionProfiles.Prudent)]
    [InlineData(ProjectionProfiles.Balanced)]
    [InlineData(ProjectionProfiles.Optimistic)]
    public void Each_profile_applied_is_derived_back(string profile)
    {
        var custom = ProjectionDefaults.Assumptions with
        {
            ClassVolatility = new Dictionary<string, decimal> { [AllocationClasses.Equity] = 0.2m },
            InflationByYear = new Dictionary<int, decimal> { [2026] = 0.03m },
        };

        var applied = ProjectionProfiles.Apply(profile, custom);

        Assert.Equal(profile, ProjectionProfiles.Of(applied));
        Assert.Equal(custom.InflationByYear, applied.InflationByYear);
    }

    [Fact]
    public void An_explicit_default_volatility_still_matches()
        => Assert.Equal(ProjectionProfiles.Balanced, ProjectionProfiles.Of(ProjectionDefaults.Assumptions with { ClassVolatility = ProjectionDefaults.ClassVolatility }));

    [Fact]
    public void A_changed_rate_is_custom()
    {
        var rates = ProjectionDefaults.ClassRates.ToDictionary(r => r.Key, r => r.Value);
        rates[AllocationClasses.Equity] = 0.07m;

        Assert.Equal(ProjectionProfiles.Custom, ProjectionProfiles.Of(ProjectionDefaults.Assumptions with { ClassRates = rates }));
    }

    [Fact]
    public void A_changed_volatility_is_custom()
        => Assert.Equal(ProjectionProfiles.Custom, ProjectionProfiles.Of(ProjectionDefaults.Assumptions with
        {
            ClassVolatility = new Dictionary<string, decimal> { [AllocationClasses.Equity] = 0.2m },
        }));
}

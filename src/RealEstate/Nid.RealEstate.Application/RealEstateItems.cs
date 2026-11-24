using Nid.Core.Contracts;

namespace Nid.RealEstate.Application;

/// <summary>#513: RealEstate's registry kinds, for the scenarios Core composes.</summary>
public sealed class RealEstateItems : IRealEstateItems
{
    public bool IsLoan(ItemAccess item) => item is { Module: "realestate", Kind: "loan" };

    public bool IsProperty(ItemAccess item) => item is { Module: "realestate", Kind: "property" };
}

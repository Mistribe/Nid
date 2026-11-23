using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using Nid.Tax.Domain;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Tax.Application;

public static class TaxCodes
{
    public const string InvalidYear = "tax.invalid_year";
    public const string InvalidProfile = "tax.invalid_profile";
    public const string InvalidInputs = "tax.invalid_inputs";
}

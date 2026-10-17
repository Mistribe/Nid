using Nid.Platform.Contracts;
using Nid.Subscriptions.Application;
using UnambitiousFx.Functional.Failures;

namespace Nid.Subscriptions.Tests;

/// <summary>The change-price command's own field paths, answered at the invoice's payload paths.</summary>
public class InvoiceProposalPathTests
{
    [Theory]
    [InlineData("startsOn", "payload.chargedOn")]
    [InlineData("price", "payload.amount.amount")]
    [InlineData("every.count", "payload")]
    public void A_command_path_becomes_a_payload_path(string path, string expected)
    {
        var failure = FieldErrors.At(new Failure("subscriptions.overlap", "Refused.", null), path);

        var mapped = InvoiceProposal.AtPayload(failure);

        var error = Assert.Single(FieldErrors.Of(mapped)!);
        Assert.Equal((expected, "subscriptions.overlap"), (error.Path, error.Code));
    }
}

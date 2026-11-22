using Nid.Ai.Contracts;
using Nid.Ai.Domain;

namespace Nid.Ai.Tests;

/// <summary>docs/ai/README.md §6.1, before each adult's own consent (§6.3, #167).</summary>
public sealed class DataPolicyTests
{
    private const string Remote = "ai.remote_not_allowed";
    private const string NoSafe = "ai.no_safe_worker";

    [Theory]
    // class, placement, safe, household opt-in, refusal
    [InlineData(DataClasses.None, WorkerPlacement.Remote, false, false, null)]
    [InlineData(DataClasses.Documents, WorkerPlacement.Local, false, false, null)]
    [InlineData(DataClasses.Documents, WorkerPlacement.Remote, false, false, Remote)]
    [InlineData(DataClasses.Documents, WorkerPlacement.Remote, false, true, null)]
    [InlineData(DataClasses.Figures, WorkerPlacement.Remote, true, false, Remote)]
    [InlineData(DataClasses.BankLabels, WorkerPlacement.Remote, false, true, null)]
    [InlineData(DataClasses.LocalOnly, WorkerPlacement.Local, false, false, NoSafe)]
    [InlineData(DataClasses.LocalOnly, WorkerPlacement.Local, true, false, null)]
    [InlineData(DataClasses.LocalOnly, WorkerPlacement.Remote, true, false, Remote)]
    [InlineData(DataClasses.LocalOnly, WorkerPlacement.Remote, true, true, null)]
    [InlineData(DataClasses.LocalOnly, WorkerPlacement.Remote, false, true, NoSafe)]
    public void A_worker_may_see_a_data_class_only_as_the_household_allows(
        string dataClass, WorkerPlacement placement, bool safe, bool remoteAllowed, string? refusal)
        => Assert.Equal(refusal, DataPolicy.Refusal(placement, safe, dataClass, remoteAllowed));

    [Fact]
    public void The_reason_names_what_would_open_a_worker()
    {
        Assert.Equal(NoSafe, DataPolicy.Reason([Remote, NoSafe]));
        Assert.Equal(Remote, DataPolicy.Reason([Remote, Remote]));
        Assert.Null(DataPolicy.Reason([]));
    }

    [Theory]
    [InlineData("documents", true)]
    [InlineData("local_only", true)]
    [InlineData("payslips", false)]
    [InlineData("", false)]
    public void Only_the_known_classes_exist(string dataClass, bool known)
        => Assert.Equal(known, DataClasses.IsKnown(dataClass));
}

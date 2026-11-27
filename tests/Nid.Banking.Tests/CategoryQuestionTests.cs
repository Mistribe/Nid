using Nid.Ai.Contracts;
using Nid.Banking.Application;
using UnambitiousFx.Functional;

namespace Nid.Banking.Tests;

/// <summary>#821: a household's categories of a side as the two steps ask them.</summary>
public class CategoryQuestionTests
{
    [Fact]
    public void A_child_whose_parent_is_out_of_use_is_a_top_and_a_side_too_large_for_a_list_is_not_asked()
    {
        Guid food = Guid.NewGuid(), groceries = Guid.NewGuid(), orphan = Guid.NewGuid();

        var tree = CategoryQuestion.Tree([(groceries, food, "Courses"), (food, null, "Alimentation"), (orphan, Guid.NewGuid(), "Cantine")])!;

        Assert.Equal([("Alimentation", 1), ("Cantine", 0)], tree.Select(t => (t.Name, t.Children.Count)));
        // 25 tops and « none of these » make 26; one more would need a lower-case id.
        Assert.NotNull(CategoryQuestion.Tree([.. Enumerable.Range(0, 25).Select(i => (Guid.NewGuid(), (Guid?)null, $"Top {i}"))]));
        Assert.Null(CategoryQuestion.Tree([.. Enumerable.Range(0, 26).Select(i => (Guid.NewGuid(), (Guid?)null, $"Top {i}"))]));
        Assert.Null(CategoryQuestion.Tree([(food, null, "Alimentation"), .. Enumerable.Range(0, 27).Select(i => (Guid.NewGuid(), (Guid?)food, $"Child {i}"))]));
    }

    [Fact]
    public void A_top_reads_with_its_children_names_within_an_option_length()
    {
        var food = Guid.NewGuid();
        var top = CategoryQuestion.Tree([(food, null, "Alimentation"), .. Enumerable.Range(0, 20).Select(i => (Guid.NewGuid(), (Guid?)food, $"Sous-catégorie {i:00}"))])!
            .Single();

        var text = CategoryQuestion.TopText(top);

        Assert.StartsWith("Alimentation (Sous-catégorie 00, Sous-catégorie 01", text, StringComparison.Ordinal);
        Assert.Equal(200, text.Length);
    }

    /// <summary>#812: a cloud provider gives calibrated probabilities and bills each text: asked once over the leaves, no content-free text,
    /// under a strategy of its own.</summary>
    [Fact]
    public async Task A_cloud_provider_is_asked_once_over_the_leaves_without_content_free_texts()
    {
        Guid food = Guid.NewGuid(), groceries = Guid.NewGuid(), delivery = Guid.NewGuid(), gifts = Guid.NewGuid();
        var tree = CategoryQuestion.Tree([(food, null, "Alimentation"), (groceries, food, "Courses"), (delivery, food, "Livraison"), (gifts, null, "Cadeaux")])!;
        var cloud = new Cloud();

        var asked = await CategoryQuestion.AskAsync(cloud, ["Sens : débit\nMotif : COURSES"], true, tree, DataClasses.BankLabels, [], oneStep: true,
            TestContext.Current.CancellationToken);

        var request = Assert.Single(cloud.Requests);
        Assert.Equal(["Sens : débit\nMotif : COURSES"], request.Texts);
        Assert.Equal(["Alimentation › Courses", "Alimentation › Livraison", "Cadeaux", CategoryGuesser.NoneOfThese], request.Options);
        Assert.True(asked.TryGetValue(out var answer));
        Assert.Equal(new DecideKey("jev-choice", "1/3", "jev", "cloud:jev-choice"), answer.Key);
        var guess = Assert.Single(answer.Guesses);
        Assert.Equal((groceries, (Guid?)null), (guess.Choice, guess.Then?.Choice));
        Assert.Equal([(groceries, Math.Log(0.9)), (null, Math.Log(0.1))], guess.Scores);
        Assert.Equal("1/2", CategoryQuestion.Version("1", "sha256:abc"));
    }

    private sealed class Cloud : IDecisions
    {
        public List<LlmDecideRequest> Requests { get; } = [];

        public Task<string?> ModelAsync(CancellationToken ct) => Task.FromResult<string?>("jev");

        public Task<LlmDecideImplementation?> ImplementationAsync(string model, CancellationToken ct)
            => Task.FromResult<LlmDecideImplementation?>(new("jev-choice", "1", false));

        public Task<Result<LlmDecisions>> DecideAsync(LlmDecideRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Result.Success(new LlmDecisions("jev", "cloud:jev-choice", "jev-choice", "1", "cloud",
                [.. request.Texts.Select(_ => new LlmDecision(0, new Dictionary<int, double> { [0] = Math.Log(0.9), [request.Options.Count - 1] = Math.Log(0.1) }))])));
        }
    }
}

namespace Nid.Ai.Protocol.Contracts;

/// <summary>
/// #450: short texts, each to put in one of the same options (<c>contracts/requests/decide/v1</c>, <c>docs/ai/README.md</c> §7.1), with
/// the model the household set for <c>decide</c>. An option's id is one letter, <c>A</c>–<c>Z</c> then <c>a</c>–<c>z</c>: one token
/// for every tokenizer, where Qwen's split <c>12</c> into two digits. « None of these » is an option like the others. The answer is one
/// data frame per text, in any order.
/// </summary>
public sealed record DecideRequestInput(string Model, IReadOnlyList<DecideOption> Options, IReadOnlyList<string> Texts) : IContract
{
    public const int MaxOptions = 52;
    public const int MaxTexts = 16;
    public const int MaxChars = 400;

    /// <summary>The ids, in the order options take them.</summary>
    public static readonly IReadOnlyList<string> Ids = [.. Enumerable.Range('A', 26).Concat(Enumerable.Range('a', 26)).Select(c => ((char)c).ToString())];

    public bool IsValid()
        => Model.Length is > 0 and <= 200
           && Options.Count is > 0 and <= MaxOptions && Options.All(o => o?.IsValid() == true) && Options.Select(o => o.Id).Distinct().Count() == Options.Count
           && Texts.Count is > 0 and <= MaxTexts && Texts.All(t => t is { Length: > 0 and <= MaxChars });
}

/// <summary>An option: its one-letter id and its name as the model reads it (<c>Alimentation › Courses</c>).</summary>
public sealed record DecideOption(string Id, string Name) : IContract
{
    public bool IsValid() => DecideRequestInput.Ids.Contains(Id) && Name.Length is > 0 and <= 200;
}

/// <summary>
/// The answer for the text at <see cref="Index"/>: the option the model chose (null when its answer is no option id), the raw score of
/// each option it scored (a log-probability for <see cref="DecideScores.Logprobs"/>; none when the implementation gives none), and what
/// answered: the model's digest and the implementation with its version, prompt included.
/// </summary>
public sealed record DecideRequestOutput(
    int Index,
    string? Choice,
    IReadOnlyList<DecideScore> Scores,
    string Digest,
    string Implementation,
    string ImplementationVersion) : IContract
{
    public bool IsValid()
        => Index is >= 0 and < DecideRequestInput.MaxTexts
           && (Choice is null || DecideRequestInput.Ids.Contains(Choice))
           && Scores.Count <= DecideRequestInput.MaxOptions && Scores.All(s => s?.IsValid() == true) && Scores.Select(s => s.Id).Distinct().Count() == Scores.Count
           && Digest.Length is > 0 and <= 200 && Implementation.Length is > 0 and <= 100 && ImplementationVersion.Length is > 0 and <= 100;
}

public sealed record DecideScore(string Id, double Score) : IContract
{
    public bool IsValid() => DecideRequestInput.Ids.Contains(Id) && double.IsFinite(Score);
}

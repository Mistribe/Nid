namespace Nid.Ai.Protocol.Contracts;

/// <summary>A batch of texts to embed (<c>contracts/requests/embed/v1</c>, #558) with the model the household set for
/// <c>embed</c>. The answer is one data frame per text, in any order.</summary>
public sealed record EmbedRequestInput(string Model, IReadOnlyList<string> Texts) : IContract
{
    public const int MaxTexts = 64;
    public const int MaxChars = 32_000;

    public bool IsValid()
        => Model.Length is > 0 and <= 200
           && Texts.Count is > 0 and <= MaxTexts && Texts.All(t => t is { Length: > 0 and <= MaxChars });
}

/// <summary>The vector of the text at <see cref="Index"/> in the batch.</summary>
public sealed record EmbedRequestOutput(int Index, IReadOnlyList<float> Vector) : IContract
{
    public const int MaxDimension = 4096;

    public bool IsValid() => Index is >= 0 and < EmbedRequestInput.MaxTexts && Vector.Count is > 0 and <= MaxDimension;
}

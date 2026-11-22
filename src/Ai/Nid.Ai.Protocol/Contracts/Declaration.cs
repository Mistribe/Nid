namespace Nid.Ai.Protocol.Contracts;

/// <summary>What a worker says it serves (<c>docs/ai/README.md</c> §3.2), sealed like any response. Placement and the
/// <c>safe</c> tag never come from here, and a model's name and digest are the worker's word (decision V).</summary>
public sealed record WorkerDeclaration(
    string SoftwareVersion,
    IReadOnlyList<ContractVersion> Jobs,
    IReadOnlyList<ContractVersion> Requests,
    int MaxConcurrentRequests,
    IReadOnlyList<DeclaredModel> Models,
    string NidKeyFingerprint) : IContract
{
    public bool IsValid()
        => SoftwareVersion.Length is > 0 and <= 64
           && MaxConcurrentRequests is >= 1 and <= 1024
           && Jobs.Count <= 100 && Requests.Count <= 100 && Models.Count <= 100
           && NidKeyFingerprint.Length == 64 && NidKeyFingerprint.All(char.IsAsciiHexDigitLower)
           && Jobs.All(c => c?.IsValid() == true)
           && Requests.All(c => c?.IsValid() == true)
           && Models.All(m => m?.IsValid() == true);
}

/// <summary>A job or request type and the contract version served: <c>contracts/{jobs,requests}/{type}/v{version}</c>.</summary>
public sealed record ContractVersion(string Type, int Version) : IContract
{
    public bool IsValid() => Type.Length is > 0 and <= 64 && Version >= 1;
}

public enum DeclaredCapability
{
    DocumentText,
    Embed,
    Decide,
    Chat,
}

/// <summary>One model behind a capability. <c>dimension</c> is required for <c>embed</c>, <c>decide</c> for
/// <c>decide</c>.</summary>
public sealed record DeclaredModel(
    DeclaredCapability Capability,
    string Name,
    string Digest,
    int ContextTokens,
    int MaxOutputTokens,
    int? Dimension = null,
    bool? Tools = null,
    bool? StructuredOutput = null,
    DecideImplementation? Decide = null) : IContract
{
    public bool IsValid()
        => Name.Length is > 0 and <= 200 && Digest.Length is > 0 and <= 200 && ContextTokens > 0 && MaxOutputTokens > 0
           && (Capability != DeclaredCapability.Embed || Dimension > 0)
           && (Capability != DeclaredCapability.Decide || Decide?.IsValid() == true);
}

public enum DecideScores
{
    Logprobs,
    Native,
    None,
}

/// <summary>How a worker implements <c>decide</c> (prompt included in its version) and the scores it returns.</summary>
public sealed record DecideImplementation(string Name, string Version, DecideScores Scores) : IContract
{
    public bool IsValid() => Name.Length is > 0 and <= 100 && Version.Length is > 0 and <= 100;
}

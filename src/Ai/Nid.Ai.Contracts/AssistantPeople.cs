namespace Nid.Ai.Contracts;

/// <summary>
/// #533 (<c>docs/ai/README.md</c> §6.2, §6.3): the assistant as one adult set it for themselves. <see cref="Enabled"/>: they
/// turned it on, which is their consent for their own data off the premises. <see cref="OffsiteDefault"/>: <c>ask</c>,
/// <c>allow</c> or <c>never</c>; <c>never</c> holds back every item they own. <see cref="CloudPossible"/>: the household's
/// <c>chat</c> use may reach a cloud provider.
/// </summary>
public sealed record AssistantMeView(bool Enabled, string OffsiteDefault, bool CloudPossible);

public static class OffsitePreferences
{
    public const string Ask = "ask";
    public const string Allow = "allow";
    public const string Never = "never";

    public static readonly IReadOnlyList<string> All = [Ask, Allow, Never];
}

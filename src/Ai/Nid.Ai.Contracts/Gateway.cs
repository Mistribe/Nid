namespace Nid.Ai.Contracts;

/// <summary>What the household uses models for (decision H, <c>docs/ai/README.md</c> §5.3): each one set on its own.</summary>
public static class ModelUses
{
    public const string Chat = "chat";
    public const string Embed = "embed";
    public const string Decide = "decide";
    public const string Extract = "extract";

    public static readonly IReadOnlyList<string> All = [Chat, Embed, Decide, Extract];

    /// <summary>The capability a worker declares for the models of a use (<c>contracts/workers/declaration</c>).</summary>
    public static string Capability(string use) => use switch
    {
        Embed => "embed",
        Decide => "decide",
        _ => "chat",
    };

    /// <summary>The uses whose models answer a chat turn (the <c>chat</c> task, tools included only for <see cref="Chat"/>).</summary>
    public static bool TakesChat(string use) => use is Chat or Extract;
}

/// <summary>How far a use may go: nowhere, the household's workers, or its workers then an enabled cloud provider.</summary>
public static class UseModes
{
    public const string Off = "off";
    public const string SelfHosted = "self_hosted";
    public const string SelfHostedThenCloud = "self_hosted_then_cloud";

    public static readonly IReadOnlyList<string> All = [Off, SelfHosted, SelfHostedThenCloud];

    /// <summary>The default of a use (<c>docs/ai/README.md</c> §5.3): self-hosted, no cloud provider until the household asks
    /// for one (#172); <c>decide</c> off until its feature (#450).</summary>
    public static string DefaultFor(string use) => use == ModelUses.Decide ? Off : SelfHosted;
}

/// <summary>One use: how far it may go (any adult) and the model it runs on (an admin, among the declared models).</summary>
public sealed record UseSettingView(string Use, string Mode, string? Model);

/// <summary>A model the paired workers declare: its name, the capability it serves, and how many active workers serve it.</summary>
public sealed record DeclaredModelView(string Name, string Capability, int Workers);

public sealed record DeclaredModels(IReadOnlyList<DeclaredModelView> Models);

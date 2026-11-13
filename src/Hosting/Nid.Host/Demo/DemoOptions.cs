namespace Nid.Host.Demo;

/// <summary>
/// Issue 203: a public demo of Nid, off by default (<c>Nid__Demo__Enabled=true</c>). With it on, no identity provider is
/// needed: <c>/auth/login</c> leads to the demo page, which signs in as one adult of the demo household
/// (<see cref="DemoHousehold"/>), and an empty database is filled with that household on start. Bank sync, outgoing
/// email, webhooks and access tokens are off (<see cref="DemoGuardMiddleware"/>).
/// </summary>
public sealed class DemoOptions
{
    public const string Section = "Nid:Demo";

    public bool Enabled { get; set; }

    public static DemoOptions Load(IConfiguration configuration)
        => configuration.GetSection(Section).Get<DemoOptions>() ?? new DemoOptions();
}

namespace Nid.Ai.Protocol.Contracts;

/// <summary>The test job (<c>contracts/jobs/echo/v1</c>): the worker returns the text it received.</summary>
public sealed record EchoJobInput(string Text) : IContract
{
    public bool IsValid() => Text.Length <= 1000;
}

public sealed record EchoJobOutput(string Text) : IContract
{
    public bool IsValid() => Text.Length <= 1000;
}

/// <summary>The test request (<c>contracts/requests/echo/v1</c>): the worker streams the text back in
/// <see cref="Frames"/> data frames, one <see cref="EchoRequestOutput"/> each.</summary>
public sealed record EchoRequestInput(string Text, int Frames) : IContract
{
    public bool IsValid() => Text.Length <= 1000 && Frames is >= 1 and <= 100;
}

public sealed record EchoRequestOutput(string Text) : IContract
{
    public bool IsValid() => Text.Length <= 1000;
}

using Nid.Platform.Contracts;

namespace Nid.Platform.Tests;

/// <summary>#250: a Problem Details' JSON Pointer read back as the path the rules and the MCP refusals name.</summary>
public class FieldErrorsPathTests
{
    [Theory]
    [InlineData("#/amount", "amount")]
    [InlineData("#/payload/movements/3/amount", "payload.movements[3].amount")]
    [InlineData("#/weekTypes/0/days/2", "weekTypes[0].days[2]")]
    [InlineData("#/a~1b/c~0d", "a/b.c~d")]
    [InlineData("#/first%20name", "first name")]
    [InlineData("/amount", "amount")]
    [InlineData("#", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Reads_a_pointer_as_a_path(string? pointer, string path)
        => Assert.Equal(path, FieldErrors.PathOf(pointer));
}

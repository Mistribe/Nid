using System.Text;
using Nid.Ai.Protocol;
using Nid.Ai.Protocol.Contracts;

namespace Nid.Ai.Tests;

/// <summary>The records are the contract; the committed schemas in <c>contracts/</c> are generated from them for any other
/// implementation, and a drift between the two fails here.</summary>
public sealed class ContractSchemaTests
{
    public static TheoryData<string, string> Schemas() => new()
    {
        { nameof(WorkerDeclaration), "contracts/workers/declaration/v1/schema.json" },
        { nameof(EchoJobInput), "contracts/jobs/echo/v1/input.schema.json" },
        { nameof(EchoJobOutput), "contracts/jobs/echo/v1/output.schema.json" },
        { nameof(EchoRequestInput), "contracts/requests/echo/v1/input.schema.json" },
        { nameof(EchoRequestOutput), "contracts/requests/echo/v1/output.schema.json" },
    };

    [Theory]
    [MemberData(nameof(Schemas))]
    public void Committed_schemas_match_the_records(string record, string path)
    {
        var generated = ContractJson.SchemaText(typeof(WorkerDeclaration).Assembly.GetType($"Nid.Ai.Protocol.Contracts.{record}")!);
        var file = Path.Combine(Vectors.RepositoryRoot(), path);

        if (Environment.GetEnvironmentVariable("NID_REGENERATE_CONTRACTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, generated);
        }

        Assert.True(File.Exists(file) && File.ReadAllText(file).ReplaceLineEndings("\n") == generated,
            $"{path} differs from {record}: rerun with NID_REGENERATE_CONTRACTS=1 and commit the file.");
    }

    [Fact]
    public void A_valid_declaration_is_read()
    {
        var declaration = ContractJson.Read<WorkerDeclaration>(Declaration(Model("embed", ", \"dimension\": 768")));

        Assert.Equal(DeclaredCapability.Embed, Assert.Single(declaration.Models).Capability);
        Assert.Equal(768, declaration.Models[0].Dimension);
    }

    [Theory]
    [InlineData("{\"text\": \"hi\", \"frames\": 2, \"extra\": 1}")] // unknown field
    [InlineData("{\"frames\": 2}")] // missing required field
    [InlineData("{\"text\": \"hi\", \"frames\": 0}")] // out of range
    [InlineData("{\"text\": \"hi\", \"frames\": 101}")]
    [InlineData("{\"text\": null, \"frames\": 2}")] // null where none is allowed
    [InlineData("not json")]
    [InlineData("{\"text\": \"hi\", \"frames\": 200, \"frames\": 2}")] // duplicate key: two readers could disagree
    public void A_bad_echo_request_is_refused(string json)
        => Assert.Equal("contract_invalid",
            Assert.Throws<ProtocolException>(() => ContractJson.Read<EchoRequestInput>(Encoding.UTF8.GetBytes(json))).Code);

    [Theory]
    [InlineData("speak", "")] // unknown capability
    [InlineData("embed", "")] // embed without its dimension
    [InlineData("decide", "")] // decide without its implementation
    public void A_bad_model_is_refused(string capability, string extra)
        => Assert.Equal("contract_invalid",
            Assert.Throws<ProtocolException>(() => ContractJson.Read<WorkerDeclaration>(Declaration(Model(capability, extra)))).Code);

    [Fact]
    public void A_null_list_element_is_refused()
    {
        var json = Encoding.UTF8.GetString(Declaration(Model("chat", ""))).Replace("[{\"type\": \"echo\", \"version\": 1}]", "[null]");

        Assert.Equal("contract_invalid",
            Assert.Throws<ProtocolException>(() => ContractJson.Read<WorkerDeclaration>(Encoding.UTF8.GetBytes(json))).Code);
    }

    private static string Model(string capability, string extra)
        => $$"""{"capability": "{{capability}}", "name": "m", "digest": "sha256:ab", "contextTokens": 8192, "maxOutputTokens": 1024{{extra}}}""";

    private static byte[] Declaration(string model) => Encoding.UTF8.GetBytes($$"""
        {
          "softwareVersion": "0.1.0",
          "jobs": [{"type": "echo", "version": 1}],
          "requests": [{"type": "echo", "version": 1}],
          "maxConcurrentRequests": 2,
          "models": [{{model}}],
          "nidKeyFingerprint": "{{new string('a', 64)}}"
        }
        """);
}

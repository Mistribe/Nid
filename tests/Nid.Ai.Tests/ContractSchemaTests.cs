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
        { nameof(DocumentTextInput), "contracts/jobs/document-text/v1/input.schema.json" },
        { nameof(DocumentTextOutput), "contracts/jobs/document-text/v1/output.schema.json" },
        { nameof(EmbedRequestInput), "contracts/requests/embed/v1/input.schema.json" },
        { nameof(EmbedRequestOutput), "contracts/requests/embed/v1/output.schema.json" },
        { nameof(DecideRequestInput), "contracts/requests/decide/v1/input.schema.json" },
        { nameof(DecideRequestOutput), "contracts/requests/decide/v1/output.schema.json" },
        { nameof(EchoRequestInput), "contracts/requests/echo/v1/input.schema.json" },
        { nameof(EchoRequestOutput), "contracts/requests/echo/v1/output.schema.json" },
        { nameof(ChatRequestInput), "contracts/requests/chat/v1/input.schema.json" },
        { nameof(ChatRequestOutput), "contracts/requests/chat/v1/output.schema.json" },
        { nameof(PairRequest), "contracts/workers/pair-request/v1/schema.json" },
        { nameof(PairResponse), "contracts/workers/pair-response/v1/schema.json" },
        { nameof(RotateWorkerKeyResponse), "contracts/workers/rotate-worker-key-response/v1/schema.json" },
        { nameof(RotateNidKeyRequest), "contracts/workers/rotate-nid-key-request/v1/schema.json" },
        { nameof(JobSubmission), "contracts/workers/job-submission/v1/schema.json" },
        { nameof(JobAccepted), "contracts/workers/job-accepted/v1/schema.json" },
        { nameof(JobPoll), "contracts/workers/job-poll/v1/schema.json" },
        { nameof(JobStatus), "contracts/workers/job-status/v1/schema.json" },
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
    public void A_declaration_with_an_overlong_text_is_refused()
    {
        var json = Encoding.UTF8.GetString(Declaration(Model("chat", ""))).Replace("\"0.1.0\"", $"\"{new string('9', 65)}\"");

        Assert.Equal("contract_invalid",
            Assert.Throws<ProtocolException>(() => ContractJson.Read<WorkerDeclaration>(Encoding.UTF8.GetBytes(json))).Code);
    }

    [Theory]
    [InlineData("""{"type": "echo", "version": 1, "input": {"text": "hi"}, "file": null}""", true)]
    [InlineData("""{"type": "echo", "version": 0, "input": {}, "file": null}""", false)]
    [InlineData("""{"type": "", "version": 1, "input": {}, "file": null}""", false)]
    [InlineData("""{"type": "echo", "version": 1, "input": {}, "file": "not base64!"}""", false)]
    public void A_job_submission_is_read_strictly(string json, bool valid)
    {
        if (valid)
            Assert.Equal("echo", ContractJson.Read<JobSubmission>(Encoding.UTF8.GetBytes(json)).Type);
        else
            Assert.Equal("contract_invalid", Assert.Throws<ProtocolException>(() => ContractJson.Read<JobSubmission>(Encoding.UTF8.GetBytes(json))).Code);
    }

    [Theory]
    [InlineData("""{"status": "succeeded", "error": null, "resultLength": 120}""", true)]
    [InlineData("""{"status": "running", "error": null, "resultLength": null}""", true)]
    [InlineData("""{"status": "done", "error": null, "resultLength": null}""", false)]
    [InlineData("""{"status": "succeeded", "error": null, "resultLength": null}""", false)]
    [InlineData("""{"status": "succeeded", "error": null, "resultLength": 25165825}""", false)]
    public void A_job_status_is_read_strictly(string json, bool valid)
    {
        if (valid)
            Assert.NotNull(ContractJson.Read<JobStatus>(Encoding.UTF8.GetBytes(json)));
        else
            Assert.Equal("contract_invalid", Assert.Throws<ProtocolException>(() => ContractJson.Read<JobStatus>(Encoding.UTF8.GetBytes(json))).Code);
    }

    [Fact]
    public void A_declaration_may_report_its_load()
        => Assert.Equal(3, ContractJson.Read<WorkerDeclaration>(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(Declaration(Model("chat", ""))).Replace("\"maxConcurrentRequests\": 2,", "\"maxConcurrentRequests\": 2, \"load\": 3,"))).Load);

    [Fact]
    public void An_output_with_a_nul_character_is_not_kept()
    {
        var contract = new Nid.Ai.Application.EchoJobContract();

        Assert.Null(contract.ReadOutput("""{"text": "a\u0000b"}"""u8));
        Assert.NotNull(contract.ReadOutput("""{"text": "ab"}"""u8));
    }

    [Fact]
    public void A_null_list_element_is_refused()
    {
        var json = Encoding.UTF8.GetString(Declaration(Model("chat", ""))).Replace("[{\"type\": \"echo\", \"version\": 1}]", "[null]");

        Assert.Equal("contract_invalid",
            Assert.Throws<ProtocolException>(() => ContractJson.Read<WorkerDeclaration>(Encoding.UTF8.GetBytes(json))).Code);
    }

    [Theory]
    [InlineData("ABCD-EFGH")] // the separator is display only
    [InlineData("ABCDEFG")] // too short
    [InlineData("ABCDEFGI")] // I is not in the alphabet
    public void A_bad_pairing_code_is_refused(string code)
        => Assert.Equal("contract_invalid", Assert.Throws<ProtocolException>(() =>
            ContractJson.Read<PairRequest>(Encoding.UTF8.GetBytes($$"""{"nidPublicKey": "{{Spki}}", "code": "{{code}}"}"""))).Code);

    [Fact]
    public void A_pairing_request_carries_a_P256_key()
    {
        Assert.Equal("ABCDEFGH", ContractJson.Read<PairRequest>(
            Encoding.UTF8.GetBytes($$"""{"nidPublicKey": "{{Spki}}", "code": "ABCDEFGH"}""")).Code);
        Assert.Equal("contract_invalid", Assert.Throws<ProtocolException>(() => ContractJson.Read<PairRequest>(
            Encoding.UTF8.GetBytes("""{"nidPublicKey": "AAAA", "code": "ABCDEFGH"}"""))).Code);
    }

    private static readonly string Spki = Convert.ToBase64String(
        System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfo());

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

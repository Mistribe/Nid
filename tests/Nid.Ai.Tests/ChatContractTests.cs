using System.Text;
using Nid.Ai.Protocol;
using Nid.Ai.Protocol.Contracts;

namespace Nid.Ai.Tests;

/// <summary>#485: a chat turn is read only when it makes sense as a conversation, and an output frame carries one thing.</summary>
public sealed class ChatContractTests
{
    private const string Tool = """{"name": "list_envelopes", "description": "", "parameters": {"type": "object"}}""";

    [Fact]
    public void A_turn_with_a_tool_answer_is_read()
    {
        var input = ContractJson.Read<ChatRequestInput>(Encoding.UTF8.GetBytes($$"""
            {"model": "llama3.2", "maxTokens": 256, "tools": [{{Tool}}], "messages": [
              {"role": "user", "content": "Bonjour"},
              {"role": "assistant", "content": null, "toolCalls": [{"id": "c1", "name": "list_envelopes", "arguments": "{}"}]},
              {"role": "tool", "content": "[]", "toolCallId": "c1"}]}
            """));

        Assert.Equal(ChatRole.Tool, input.Messages[^1].Role);
    }

    [Theory]
    [InlineData("""{"model": "m", "maxTokens": 256, "tools": [], "messages": []}""")] // no message
    [InlineData("""{"model": "m", "maxTokens": 0, "tools": [], "messages": [{"role": "user", "content": "x"}]}""")]
    [InlineData("""{"model": "m", "maxTokens": 256, "temperature": 3, "tools": [], "messages": [{"role": "user", "content": "x"}]}""")]
    [InlineData("""{"model": "m", "maxTokens": 256, "tools": [], "messages": [{"role": "tool", "content": "x"}]}""")] // a tool answer without its call
    [InlineData("""{"model": "m", "maxTokens": 256, "tools": [], "messages": [{"role": "user", "content": null}]}""")]
    [InlineData("""{"model": "m", "maxTokens": 256, "tools": [], "messages": [{"role": "user", "content": "x", "toolCalls": [{"id": "c", "name": "t", "arguments": "{}"}]}]}""")]
    [InlineData("""{"model": "m", "maxTokens": 256, "tools": [{"name": "bad name", "description": "", "parameters": {}}], "messages": [{"role": "user", "content": "x"}]}""")]
    [InlineData("""{"model": "m", "maxTokens": 256, "tools": [{"name": "t", "description": "", "parameters": []}], "messages": [{"role": "user", "content": "x"}]}""")]
    public void A_turn_that_makes_no_sense_is_refused(string json)
        => Assert.Equal("contract_invalid", Assert.Throws<ProtocolException>(() => ContractJson.Read<ChatRequestInput>(Encoding.UTF8.GetBytes(json))).Code);

    [Fact]
    public void The_same_tool_twice_is_refused()
        => Assert.Throws<ProtocolException>(() => ContractJson.Read<ChatRequestInput>(Encoding.UTF8.GetBytes(
            $$"""{"model": "m", "maxTokens": 256, "tools": [{{Tool}}, {{Tool}}], "messages": [{"role": "user", "content": "x"}]}""")));

    [Theory]
    [InlineData("""{"text": "a", "toolCall": null, "usage": {"tokensIn": 1, "tokensOut": 1, "stop": "end", "model": "m"}}""")]
    [InlineData("""{"text": null, "toolCall": null, "usage": null}""")]
    public void An_output_frame_carries_exactly_one_thing(string json)
        => Assert.Throws<ProtocolException>(() => ContractJson.Read<ChatRequestOutput>(Encoding.UTF8.GetBytes(json)));
}

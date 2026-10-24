using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OneWare.Codex.Protocol;
using OneWare.Codex.Services;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class McpBoundaryTests
{
    [Fact]
    public async Task EndpointUsesOnlyMcpDescriptorsAndForwardsInvocationMetadataToItsOwner()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]}""");
        OneWareMcpTool[] tools = [new("zeta", "last", schema.RootElement, false), new("alpha", null, schema.RootElement, true)];
        var calls = new List<(string Name, JsonElement Arguments, JsonElement Meta)>();
        await using var server = new OneWareMcpServer(tools, (name, args, meta, _) =>
        {
            calls.Add((name, args.Clone(), meta.Clone()));
            return Task.FromResult(OneWareMcpResult.Success(new TextContent("native reply")));
        });
        // The catalog must retain JSON values after the descriptor's source document is disposed.
        schema.Dispose();
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Authorization", server.Authorization);
        async Task<JsonElement> Request(string method, object parameters)
        {
            using var response = await client.PostAsJsonAsync(server.Url, new { jsonrpc = "2.0", id = 7, method, @params = parameters });
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("result").Clone();
        }
        var list = (await Request("tools/list", new { })).GetProperty("tools");
        Assert.Equal(new[] { "alpha", "zeta" }, list.EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        Assert.Equal("alpha", list[0].GetProperty("description").GetString());
        Assert.True(list[0].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.False(list[1].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.Equal("value", list[0].GetProperty("inputSchema").GetProperty("required")[0].GetString());
        var result = await Request("tools/call", new { name = "alpha", arguments = new { value = "payload" },
            _meta = new { threadId = "owner-session", callId = "owner-call", extra = new { sequence = 2 } } });
        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.Equal("native reply", result.GetProperty("content")[0].GetProperty("text").GetString());
        var call = Assert.Single(calls);
        Assert.Equal("alpha", call.Name);
        Assert.Equal("payload", call.Arguments.GetProperty("value").GetString());
        Assert.Equal("owner-session", call.Meta.GetProperty("threadId").GetString());
        Assert.Equal("owner-call", call.Meta.GetProperty("callId").GetString());
        Assert.Equal(2, call.Meta.GetProperty("extra").GetProperty("sequence").GetInt32());
        var url = server.Url;
        var equivalent = list.EnumerateArray().Reverse().Select(t => new OneWareMcpTool(t.GetProperty("name").GetString()!,
            t.GetProperty("description").GetString(), t.GetProperty("inputSchema"), t.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean())).ToArray();
        Assert.True(server.Matches(equivalent));
        server.Publish(equivalent);
        Assert.Equal(url, server.Url);
        server.Publish([]);
        Assert.False(server.Matches(equivalent));
        Assert.NotEqual(url, server.Url);
        var removed = await Request("tools/call", new { name = "alpha", arguments = new { value = "payload" } });
        Assert.True(removed.GetProperty("isError").GetBoolean());
        Assert.Single(calls);
    }

    [Fact]
    public void ResultEncoderKeepsExactMixedTextImageAndErrorContracts()
    {
        AIContent[] content = [new TextContent("before"), new DataContent(new byte[] { 0, 255 }, "image/png"), new TextContent("after")];
        Assert.Equal("""{"isError":false,"content":[{"type":"text","text":"before"},{"type":"image","mimeType":"image/png","data":"AP8="},{"type":"text","text":"after"}]}""",
            JsonSerializer.Serialize(OneWareMcpResult.Success(content)));
        Assert.Equal("""{"isError":true,"content":[{"type":"text","text":"denied"}]}""",
            JsonSerializer.Serialize(OneWareMcpResult.Failure("denied")));
    }

    [Fact]
    public void EncoderRetainsJsonFallbackAndEmptyTypedContent()
    {
        foreach (var value in new object?[] { null, true, "quoted", new { result = "source text", error = (string?)null } })
        {
            var result = JsonSerializer.SerializeToElement(OneWareMcpResult.Success(value));
            var item = Assert.Single(result.GetProperty("content").EnumerateArray());
            Assert.Equal("text", item.GetProperty("type").GetString());
            Assert.Equal(JsonSerializer.Serialize(value), item.GetProperty("text").GetString());
        }
        Assert.Empty(JsonSerializer.SerializeToElement(OneWareMcpResult.Success(Array.Empty<AIContent>())).GetProperty("content").EnumerateArray());
        Assert.Equal("unquoted", JsonSerializer.SerializeToElement(OneWareMcpResult.Success(new TextContent("unquoted")))
            .GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void UnsupportedBinaryStillFailsInsteadOfBecomingSuccessfulJson()
    {
        var value = new DataContent(new byte[] { 1 }, "application/octet-stream");
        var error = Assert.Throws<NotSupportedException>(() => OneWareMcpResult.Success(value));
        Assert.Equal("Codex cannot return this tool content: DataContent.", error.Message);
    }
}

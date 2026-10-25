using System.Text.Json;
using Microsoft.Extensions.AI;
using OneWare.Codex.Protocol;
using Xunit;

namespace OneWare.Codex.UnitTests;

[Trait("Contract", "F07")]
public sealed class OneWareMcpResultTests
{
    private static JsonElement Encode(object? value) => JsonSerializer.SerializeToElement(OneWareMcpResult.Success(value));
    private static JsonElement Items(JsonElement result) => result.GetProperty("content");

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    public void MixedContentKeepsOrderMimeAndExactBytes(string mime)
    {
        byte[] bytes = [0, 1, 2, 127, 128, 254, 255];
        AIContent[] source = [new TextContent("before"), new DataContent(bytes, mime), new TextContent("after")];
        var result = Encode(source);
        var items = Items(result);
        Assert.Equal(3, items.GetArrayLength());
        Assert.Equal("before", items[0].GetProperty("text").GetString());
        Assert.Equal("after", items[2].GetProperty("text").GetString());
        Assert.Equal("image", items[1].GetProperty("type").GetString());
        Assert.Equal(mime, items[1].GetProperty("mimeType").GetString());
        Assert.Equal(bytes, Convert.FromBase64String(items[1].GetProperty("data").GetString()!));
        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.False(result.TryGetProperty("contentItems", out _));
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
        foreach (var value in new object?[] { null, true, "quoted", Array.Empty<string>(), new { result = "source text", error = (string?)null },
                     new { success = false, error = "domain failure" } })
        {
            var result = JsonSerializer.SerializeToElement(OneWareMcpResult.Success(value));
            Assert.False(result.GetProperty("isError").GetBoolean());
            var item = Assert.Single(result.GetProperty("content").EnumerateArray());
            Assert.Equal("text", item.GetProperty("type").GetString());
            Assert.Equal(JsonSerializer.Serialize(value), item.GetProperty("text").GetString());
        }
        Assert.Empty(JsonSerializer.SerializeToElement(OneWareMcpResult.Success(Array.Empty<AIContent>())).GetProperty("content").EnumerateArray());
        Assert.Equal("unquoted", JsonSerializer.SerializeToElement(OneWareMcpResult.Success(new TextContent("unquoted")))
            .GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("invalid arguments")]
    [InlineData("invocation exception")]
    public void ExplicitFailureKeepsItsFlagAndExplanation(string explanation)
    {
        var result = JsonSerializer.SerializeToElement(OneWareMcpResult.Failure(explanation));
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal(explanation, Items(result)[0].GetProperty("text").GetString());
        Assert.Equal("text", Items(result)[0].GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedBinaryThrowsInsteadOfReturningEmptySuccess(bool mixed)
    {
        var binary = new DataContent(new byte[] { 1, 2, 3 }, "application/octet-stream");
        object source = mixed ? new AIContent[] { new TextContent("before"), binary, new TextContent("after") } : binary;
        var exception = Assert.Throws<NotSupportedException>(() => Encode(source));
        Assert.Equal("Codex cannot return this tool content: DataContent.", exception.Message);
        var failure = JsonSerializer.SerializeToElement(OneWareMcpResult.Failure(exception.Message));
        Assert.True(failure.GetProperty("isError").GetBoolean());
        Assert.Contains("DataContent", Items(failure)[0].GetProperty("text").GetString());
    }
}

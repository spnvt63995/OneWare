using System.Text.Json;
using Microsoft.Extensions.AI;
using OneWare.Codex.Protocol;
using Xunit;

namespace OneWare.Codex.AdapterBoundaryTests;

// Executes the current MCP codec linked from the product.
[Trait("Contract", "F07")]
public sealed class ResultEncodingTests
{
    private static JsonElement Encode(object? value) => JsonSerializer.SerializeToElement(OneWareMcpResult.Success(value));
    private static JsonElement Items(JsonElement result) => result.GetProperty("content");

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    public void F07_MixedContentKeepsOrderMimeAndExactBytes(string mime)
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
    public void F07_NullEmptyTypedAndEmptyUntypedHaveDistinctExistingMeanings()
    {
        Assert.Equal("null", Items(Encode(null))[0].GetProperty("text").GetString());
        Assert.Equal(0, Items(Encode(Array.Empty<AIContent>())).GetArrayLength());
        Assert.Equal("[]", Items(Encode(Array.Empty<string>()))[0].GetProperty("text").GetString());
        Assert.Equal("plain", Items(Encode(new TextContent("plain")))[0].GetProperty("text").GetString());
    }

    [Fact]
    public void F07_DomainErrorValueDoesNotPretendToBeATransportFailure()
    {
        var source = new { success = false, error = "domain failure" };
        var result = Encode(source);
        Assert.Equal(JsonSerializer.Serialize(source), Items(result)[0].GetProperty("text").GetString());
        Assert.False(result.GetProperty("isError").GetBoolean());
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("invalid arguments")]
    [InlineData("invocation exception")]
    public void F07_ExplicitFailureKeepsItsFlagAndExplanation(string explanation)
    {
        var result = JsonSerializer.SerializeToElement(OneWareMcpResult.Failure(explanation));
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal(explanation, Items(result)[0].GetProperty("text").GetString());
        Assert.Equal("text", Items(result)[0].GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void F07_UnsupportedBinaryThrowsInsteadOfReturningEmptySuccess(bool mixed)
    {
        var binary = new DataContent(new byte[] { 1, 2, 3 }, "application/octet-stream");
        object source = mixed ? new AIContent[] { new TextContent("before"), binary, new TextContent("after") } : binary;
        var exception = Assert.Throws<NotSupportedException>(() => Encode(source));
        Assert.Contains("DataContent", exception.Message);
        var failure = JsonSerializer.SerializeToElement(OneWareMcpResult.Failure(exception.Message));
        Assert.True(failure.GetProperty("isError").GetBoolean());
        Assert.Contains("DataContent", Items(failure)[0].GetProperty("text").GetString());
    }
}

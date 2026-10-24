using System.Text.Json;
using Xunit;

namespace OneWare.Codex.AdapterBoundaryTests;

[Trait("Contract", "F12")]
public sealed class LegacyAliasTests
{
    [Fact]
    public void F12_ResumedAliasUsesTheCurrentHandlerWithoutRewritingTheArchivedDefinition()
    {
        var calls = new List<string>();
        var old = BoundaryData.Tool(handler: _ => { calls.Add("old"); return null; });
        var current = old with { Description = "current description", ReadOnly = !old.ReadOnly,
            Handler = _ => { calls.Add("current"); return null; } };
        var f = CatalogSyncTests.Ready(current);
        f.SaveLegacyDefinition(old);
        var archived = f.ArchivedNames;
        Assert.True(f.InvokeLegacy("oneware_readFile", BoundaryData.Json("""{"path":"a"}""")));
        Assert.Equal(new[] { "current" }, calls);
        Assert.Equal(archived, f.ArchivedNames);
        Assert.Same(old, f.ArchivedDefinitions["oneware_readFile"]);
        Assert.NotEqual(current.Description, f.ArchivedDefinitions["oneware_readFile"].Description);
        Assert.NotEqual(current.ReadOnly, f.ArchivedDefinitions["oneware_readFile"].ReadOnly);
        Assert.Equal("readFile", Assert.Single(f.Invocations).Tool);
    }

    [Fact]
    public void F12_RemovedCurrentToolNeverFallsBackToTheArchivedHandler()
    {
        var calls = new List<string>();
        var old = BoundaryData.Tool(handler: _ => { calls.Add("old"); return null; });
        var f = CatalogSyncTests.Ready(old);
        f.SaveLegacyDefinition(old);
        f.Remove("readFile");
        Assert.True(f.Synchronize());
        Assert.False(f.InvokeLegacy("oneware_readFile", BoundaryData.Json("""{"path":"a"}""")));
        Assert.Empty(calls);
        Assert.Empty(f.Invocations);
        Assert.Equal(new[] { "oneware_readFile" }, f.ArchivedNames);
    }

    [Theory]
    [InlineData("""{"path":"old"}""")]
    [InlineData("""{"newPath":"a","path":"old"}""")]
    [InlineData("{}")]
    [InlineData("null")]
    public void F12_ChangedRequiredAndAdditionalArgumentsFailBeforeInvocation(string arguments)
    {
        var f = CatalogSyncTests.Ready();
        f.SaveLegacyDefinition(BoundaryData.Tool());
        CatalogSyncTests.Change(f, "schema");
        Assert.True(f.Synchronize());
        Assert.False(f.InvokeLegacy("oneware_readFile", BoundaryData.Json(arguments)));
        Assert.Empty(f.Invocations);
        Assert.True(f.InvokeLegacy("oneware_readFile", BoundaryData.Json("""{"newPath":"a"}""")));
        Assert.Single(f.Invocations);
        Assert.Equal(BoundaryData.Tool().Schema, f.ArchivedDefinitions["oneware_readFile"].Schema);
        Assert.NotEqual(f.Published["readFile"].Schema, f.ArchivedDefinitions["oneware_readFile"].Schema);
    }

    [Theory]
    [InlineData("readFile")]
    [InlineData("oneware_removed")]
    [InlineData("oneware_oneware_readFile")]
    [InlineData("ONEWARE_readFile")]
    public void F12_UnknownOrMalformedAliasesCannotExecute(string alias)
    {
        var f = CatalogSyncTests.Ready();
        Assert.False(f.InvokeLegacy(alias, BoundaryData.Json("""{"path":"a"}""")));
        Assert.Empty(f.Invocations);
    }

    [Fact]
    public void F12_CurrentPolicyStillAppliesToLegacyAliases()
    {
        var f = CatalogSyncTests.Ready();
        f.SaveLegacyDefinition(BoundaryData.Tool());
        Assert.False(f.InvokeLegacy("oneware_readFile", BoundaryData.Json("""{"path":"a"}"""), policyAllows: false));
        Assert.Empty(f.Invocations);
    }

    [Fact]
    public void F12_NewMcpSessionsDoNotSendArchivedDynamicAliases()
    {
        var f = CatalogSyncTests.Ready();
        f.SaveLegacyDefinition(BoundaryData.Tool());
        var request = JsonSerializer.SerializeToElement(f.NewMcpSessionRequest());
        Assert.Equal("oneware", request.GetProperty("mcpServers")[0].GetString());
        Assert.Equal(0, request.GetProperty("dynamicTools").GetArrayLength());
        Assert.Single(f.ArchivedNames);
    }
}

using Xunit;

namespace OneWare.Codex.AdapterBoundaryTests;

[Trait("Contract", "F11")]
public sealed class CatalogSyncTests
{
    internal static CatalogFixture Ready(FunctionSpec? tool = null)
    {
        var f = new CatalogFixture();
        f.Register(tool ?? BoundaryData.Tool());
        Assert.True(f.Synchronize());
        return f;
    }
    internal static void Change(CatalogFixture f, string change)
    {
        var tool = BoundaryData.Tool();
        switch (change)
        {
            case "add": f.Register(tool with { Name = "newTool" }); break;
            case "remove": f.Remove(tool.Name); break;
            case "description": f.Register(tool with { Description = "new description" }); break;
            case "schema": f.Register(tool with { Schema = """{"type":"object","properties":{"newPath":{}},"required":["newPath"],"additionalProperties":false}""" }); break;
            case "readOnly": f.Register(tool with { ReadOnly = false }); break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
    }

    [Fact]
    public void F11_HandlerOnlyReplacementRebindsWithoutReconnect()
    {
        var called = new List<string>();
        var old = BoundaryData.Tool(handler: _ => { called.Add("old"); return null; });
        var f = Ready(old);
        var reconnects = f.Reconnects;
        f.Register(old with { Handler = _ => { called.Add("new"); return null; } });
        Assert.True(f.Synchronize());
        Assert.Equal(reconnects, f.Reconnects);
        Assert.True(f.Invoke(f.Prepare("readFile", BoundaryData.Json("""{"path":"a"}"""))));
        Assert.Equal(new[] { "new" }, called);
    }

    [Theory]
    [InlineData("add", false)]
    [InlineData("remove", false)]
    [InlineData("description", false)]
    [InlineData("schema", false)]
    [InlineData("readOnly", false)]
    [InlineData("add", true)]
    [InlineData("remove", true)]
    [InlineData("description", true)]
    [InlineData("schema", true)]
    [InlineData("readOnly", true)]
    public void F11_EveryDescriptorChangeSynchronizesAfterTheActiveTurn(string change, bool busy)
    {
        var f = Ready();
        var reconnects = f.Reconnects;
        f.Busy = busy;
        Change(f, change);
        if (busy)
        {
            Assert.False(f.Synchronize());
            Assert.Equal(reconnects, f.Reconnects);
            f.Busy = false;
        }
        Assert.True(f.Synchronize());
        Assert.Equal(reconnects + 1, f.Reconnects);
        Assert.All(f.ResumedIds, id => Assert.Equal("session-a", id));
        Assert.True(f.Ready);
        switch (change)
        {
            case "add": Assert.Contains("newTool", f.Published.Keys); break;
            case "remove": Assert.DoesNotContain("readFile", f.Published.Keys); break;
            case "description": Assert.Equal("new description", f.Published["readFile"].Description); break;
            case "readOnly": Assert.False(f.Published["readFile"].ReadOnly); break;
            case "schema": Assert.Contains("newPath", f.Published["readFile"].Schema); break;
        }
    }

    [Theory]
    [InlineData("count")]
    [InlineData("name")]
    [InlineData("description")]
    [InlineData("schema")]
    [InlineData("readOnly")]
    public void F11_HttpSuccessWithoutMatchingBackendDescriptorNeverMarksReady(string mismatch)
    {
        var f = new CatalogFixture();
        f.Register(BoundaryData.Tool());
        f.ObserveBackend = offered =>
        {
            var observed = offered.ToDictionary();
            var tool = observed["readFile"];
            switch (mismatch)
            {
                case "count": observed.Clear(); break;
                case "name": observed["readFile"] = tool with { Name = "wrong" }; break;
                case "description": observed["readFile"] = tool with { Description = "stale" }; break;
                case "schema": observed["readFile"] = tool with { Schema = "{}" }; break;
                case "readOnly": observed["readFile"] = tool with { ReadOnly = false }; break;
            }
            return observed; // Transport succeeded; observed catalog is deliberately wrong.
        };
        Assert.False(f.Synchronize());
        Assert.False(f.Ready);
        Assert.Equal(3, f.SyncAttempts);
        Assert.NotNull(f.Error);
        Assert.Empty(f.ResumedIds);
        Assert.False(f.Invoke(f.Prepare("readFile", BoundaryData.Json("""{"path":"a"}"""))));
        Assert.Empty(f.Invocations);
    }

    [Fact]
    public void F11_ContinuousRegistrationStopsAfterABoundedNumberOfAttempts()
    {
        var f = new CatalogFixture();
        f.Register(BoundaryData.Tool());
        f.ObserveBackend = offered =>
        {
            f.Register(BoundaryData.Tool() with { Description = $"revision-{f.SyncAttempts}" });
            return offered;
        };
        Assert.False(f.Synchronize());
        Assert.Equal(3, f.SyncAttempts);
        Assert.False(f.Ready);
        Assert.Empty(f.ResumedIds);
    }

    [Fact]
    public void F11_PendingInvocationKeepsItsCapturedHandlerAndArguments()
    {
        var calls = new List<string>();
        var tool = BoundaryData.Tool(handler: call => { calls.Add("old:" + call.Arguments.GetProperty("path").GetString()); return null; });
        var f = Ready(tool);
        var pending = f.Prepare("readFile", BoundaryData.Json("""{"path":"original"}"""));
        f.Register(tool with { Handler = _ => { calls.Add("new"); return null; } });
        f.Synchronize();
        Assert.True(f.Invoke(pending));
        Assert.False(f.Invoke(pending));
        Assert.Equal(new[] { "old:original" }, calls);
        Assert.True(f.Invoke(f.Prepare("readFile", BoundaryData.Json("""{"path":"next"}"""))));
        Assert.Equal(new[] { "old:original", "new" }, calls);
    }

    [Fact]
    public void F11_EquivalentSchemaPropertyOrderDoesNotForceReconnect()
    {
        var f = Ready();
        var reconnects = f.Reconnects;
        f.Register(BoundaryData.Tool() with { Schema = """{"additionalProperties":false,"required":["path"],"properties":{"path":{"type":"string"}},"type":"object"}""" });
        Assert.True(f.Synchronize());
        Assert.Equal(reconnects, f.Reconnects);
    }
}

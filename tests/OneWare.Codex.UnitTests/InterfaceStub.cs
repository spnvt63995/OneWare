using System.Reflection;

namespace OneWare.Codex.UnitTests;

public class InterfaceStub : DispatchProxy
{
    public Func<string, object?[]?, object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!.Name, args);
    public static T Create<T>(Func<string, object?[]?, object?> handler) where T : class
    {
        var result = Create<T, InterfaceStub>();
        ((InterfaceStub)(object)result).Handler = handler;
        return result;
    }
}

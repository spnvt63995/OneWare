using OneWare.Codex.Services;
using OneWare.Essentials.Services;

namespace OneWare.Codex;

/// <summary>Registers the ACP provider and its managed runtime.</summary>
public sealed class CodexModule : OneWareModuleBase
{
    public override IReadOnlyCollection<string> Dependencies => ["ChatModule"];

    public override void Initialize(IServiceProvider serviceProvider) => AcpRegistration.Initialize(serviceProvider);
}

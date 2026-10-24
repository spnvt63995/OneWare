using System.Runtime.InteropServices;
using Avalonia.Controls;
using OneWare.Essentials.Enums;
using OneWare.Essentials.PackageManager;
using OneWare.Essentials.Services;

namespace OneWare.Codex.Services;

/// <summary>ACP-owned Node, adapter and Codex CLI packages.</summary>
internal sealed class AcpRuntime
{
    internal const string CliVersion = "0.163.0";
    internal const string AdapterVersion = "2.2.2";
    internal const string NodeVersion = "24.22.1";
    private readonly IPackageService _packages;
    private readonly IPackageWindowService _windows;
    private readonly (Package Package, string Entry)[] _components;
    public AcpProfile Profile { get; }

    internal static string NodeTarget => (OperatingSystem.IsWindows() ? "win" :
        OperatingSystem.IsMacOS() ? "darwin" : "linux") + "-" +
        (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");

    public AcpRuntime(IPaths paths, IPackageService packages, IPackageWindowService windows)
    {
        _packages = packages;
        _windows = windows;
        var node = Path.Combine(paths.NativeToolsDirectory, "codexacp-node", $"node-v{NodeVersion}-{NodeTarget}",
            OperatingSystem.IsWindows() ? "node.exe" : "bin/node");
        var adapter = Path.Combine(paths.NativeToolsDirectory, "codexacp", "package", "dist", "index.js");
        var codex = Path.Combine(paths.NativeToolsDirectory, "codexacp-cli", "bin",
            OperatingSystem.IsWindows() ? "codex.exe" : "codex");
        Profile = new AcpProfile(node, "gpt-6-luna", "low", Path.Combine(paths.AppDataDirectory, "AI", "ACP"))
        {
            Arguments = [adapter], CodexExecutable = codex
        };
        _components =
        [
            (Package("codexacp-node", "Node.js for Codex ACP", NodeVersion, "MIT and bundled licenses",
                "https://nodejs.org", (platform, target) =>
                    $"https://nodejs.org/dist/v{NodeVersion}/node-v{NodeVersion}-{target}.{(platform.StartsWith("win") ? "zip" : "tar.gz")}"), node),
            (Package("codexacp-cli", "Codex CLI for ACP", CliVersion, "Apache-2.0 and bundled licenses",
                "https://github.com/openai/codex", (_, target) =>
                    $"https://github.com/openai/codex/releases/download/rust-v{CliVersion}/codex-package-{Triple(target)}.tar.gz"), codex),
            (Package("codexacp", "Codex ACP", AdapterVersion, "Apache-2.0",
                "https://github.com/agentclientprotocol/codex-acp", (_, _) =>
                    $"https://registry.npmjs.org/@agentclientprotocol/codex-acp/-/codex-acp-{AdapterVersion}.tgz"), adapter)
        ];
        foreach (var (package, _) in _components) packages.RegisterPackage(package);
    }

    public bool IsInstalled => _components.All(x => File.Exists(x.Entry));

    public async Task<bool> NeedsUpdateAsync()
    {
        if (!_packages.IsLoaded && !await _packages.RefreshAsync(false)) return false;
        return _components.Any(x => _packages.Packages.TryGetValue(x.Package.Id!, out var state) &&
                                    state.Status == PackageStatus.UpdateAvailable);
    }

    public async Task<bool> InstallAsync(Control? owner)
    {
        if (!_packages.IsLoaded && !await _packages.RefreshAsync(false))
            throw new IOException("Could not load the Codex ACP packages. Check your connection and retry.");
        foreach (var (package, entry) in _components)
        {
            _packages.Packages.TryGetValue(package.Id!, out var state);
            if (File.Exists(entry) && state?.Status == PackageStatus.Installed) continue;
            if (!File.Exists(entry) && state?.InstalledVersion != null &&
                !await _packages.RemoveAsync(package.Id!))
                throw new IOException($"Could not repair {package.Name}.");
            if (!await _windows.QuickInstallPackageAsync(package.Id!,
                    owner == null ? null : TopLevel.GetTopLevel(owner) as Window)) return false;
            if (!File.Exists(entry)) throw new IOException($"{package.Name} did not provide its expected executable.");
        }
        return IsInstalled;
    }

    private static Package Package(string id, string name, string version, string license, string website,
        Func<string, string, string> url)
    {
        PackageTarget Target(string platform, string target) => new() { Target = platform, Url = url(platform, target) };
        return new Package
        {
        Id = id, Name = name, Type = "NativeTool", Category = "Binaries", License = license,
        Description = "Runtime component for the Codex ACP chat provider.",
        AcceptLicenseBeforeDownload = true,
        Links = [new PackageLink { Name = "Project", Url = website }],
        Versions = [new PackageVersion { Version = version, Targets =
        [
            Target("osx-arm64", "darwin-arm64"), Target("osx-x64", "darwin-x64"),
            Target("linux-arm64", "linux-arm64"), Target("linux-x64", "linux-x64"),
            Target("win-arm64", "win-arm64"), Target("win-x64", "win-x64")
        ] }]
        };
    }

    private static string Triple(string target) => target switch
    {
        "darwin-arm64" => "aarch64-apple-darwin", "darwin-x64" => "x86_64-apple-darwin",
        "win-arm64" => "aarch64-pc-windows-msvc", "win-x64" => "x86_64-pc-windows-msvc",
        "linux-arm64" => "aarch64-unknown-linux-musl", _ => "x86_64-unknown-linux-musl"
    };
}

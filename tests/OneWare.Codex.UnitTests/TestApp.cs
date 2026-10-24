using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Simple;
using OneWare.Codex.UnitTests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace OneWare.Codex.UnitTests;

public class TestApp : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

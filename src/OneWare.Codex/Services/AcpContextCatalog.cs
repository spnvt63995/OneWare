using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OneWare.Codex.Services;

internal sealed record AcpContextLimits(long DefaultTokens, long MaximumTokens);
internal sealed record ContextSizeOption(string Label, string Tier, long Tokens)
{
    public override string ToString() => Label;

    internal static string FormatTokens(long tokens) => tokens >= 1_000_000
        ? (tokens / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M"
        : tokens >= 1_000 ? (tokens / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K"
        : tokens.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Codex's own catalog supplies limits omitted from the App Server model/list response.</summary>
internal static class AcpContextCatalog
{
    internal static IReadOnlyDictionary<string, AcpContextLimits> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new Dictionary<string, AcpContextLimits>(StringComparer.Ordinal);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object ||
                !model.TryGetProperty("slug", out var slug) || slug.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(slug.GetString()) ||
                !model.TryGetProperty("context_window", out var standard) || standard.ValueKind != JsonValueKind.Number ||
                !standard.TryGetInt64(out var first) ||
                !model.TryGetProperty("max_context_window", out var maximum) || maximum.ValueKind != JsonValueKind.Number ||
                !maximum.TryGetInt64(out var last) ||
                first <= 0 || last < first || last > 9_007_199_254_740_991L) continue;
            result[slug.GetString()!] = new(first, last);
        }
        return result;
    }

    internal static async Task<IReadOnlyDictionary<string, AcpContextLimits>> ReadAsync(AcpProfile profile,
        CancellationToken token)
    {
        if (profile.CodexExecutable is not { } executable || !File.Exists(executable))
            return new Dictionary<string, AcpContextLimits>();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = profile.Directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["CODEX_HOME"] = profile.Home;
        foreach (var argument in new[] { "debug", "models", "-c", "cli_auth_credentials_store=\"file\"" })
            start.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = Process.Start(start) ?? throw new IOException("Could not read Codex model context limits.");
        using var cancelled = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var errors = ReadBoundedAsync(process.StandardError, timeout.Token);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0) throw new IOException("Codex model context limits are unavailable.");
            return Parse(await output);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
        {
            if (result.Length + count > 8 * 1024 * 1024) throw new IOException("Codex model catalog is too large.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}

using System.Text.Json;
using OneWare.Essentials.Models;

namespace OneWare.Codex.Services;

internal sealed record AcpSavedDelegation(int Version, string OwnerSession, string Directory, string Id,
    string SessionId, ChatAgentDefinition Definition, string? Description, AcpPluginCatalog Catalog,
    AcpPluginSkill[] Skills, string[] Ancestors, string? ParentId, int Turn, string State,
    string? Answer, string? Error);

internal sealed partial record AcpProfile
{
    private string DelegationDirectory(string owner)
    {
        _ = BindingPath(owner);
        return Path.Combine(Directory, "delegations", owner);
    }

    internal static bool IsAgentId(string id) => id.StartsWith("acp-agent-", StringComparison.Ordinal) &&
        Guid.TryParseExact(id[10..], "N", out var parsed) && "acp-agent-" + parsed.ToString("N") == id;

    public void SaveDelegation(AcpSavedDelegation value)
    {
        if (!IsAgentId(value.Id) || LoadBinding(value.OwnerSession) != value.Directory ||
            LoadBinding(value.SessionId) != value.Directory)
            throw new InvalidDataException("The delegated conversation does not belong to this ACP workspace.");
        var directory = DelegationDirectory(value.OwnerSession);
        System.IO.Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, value.Id + ".json");
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public (AcpSavedDelegation[] Records, bool Incomplete) LoadDelegations(string owner, string cwd)
    {
        var directory = DelegationDirectory(owner);
        if (!System.IO.Directory.Exists(directory)) return ([], false);
        var records = new List<AcpSavedDelegation>();
        var incomplete = false;
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                var value = JsonSerializer.Deserialize<AcpSavedDelegation>(File.ReadAllText(file));
                if (value is not { Version: 1, Definition.Tools: not null, Catalog.Agents: not null,
                        Catalog.Skills: not null, Skills: not null, Ancestors.Length: > 0 and <= 4 } ||
                    value.OwnerSession != owner || value.Directory != cwd || value.SessionId == owner ||
                    value.Id == null || !IsAgentId(value.Id) || Path.GetFileNameWithoutExtension(file) != value.Id ||
                    value.Turn < 1 || value.Turn == int.MaxValue ||
                    string.IsNullOrWhiteSpace(value.Definition.Id) || string.IsNullOrWhiteSpace(value.Definition.DisplayName) ||
                    value.Ancestors.Any(string.IsNullOrWhiteSpace) || value.Ancestors[^1] != value.Definition.Id ||
                    value.Ancestors.Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Ancestors.Length ||
                    value.State is not ("running" or "completed" or "failed" or "cancelled" or "interrupted") ||
                    LoadBinding(value.SessionId) != cwd)
                    throw new InvalidDataException("Invalid delegated conversation binding.");
                records.Add(value);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                incomplete = true;
            }
        }
        return (records.ToArray(), incomplete);
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using OneWare.Essentials.Models;

namespace OneWare.Codex.Services;

/// <summary>A turn snapshot of the same plugin registrations consumed by Copilot.</summary>
internal sealed record AcpPluginCatalog(OneWareAiAgent[] Agents, AcpPluginSkill[] Skills)
{
    public const string LoadSkillTool = "oneware_load_skill";
    public const string DelegateTool = "oneware_delegate";
    public const string AgentResultTool = "oneware_agent_result";
    public const string FollowupTool = "oneware_agent_followup";

    public static bool IsPluginTool(string name) => name is LoadSkillTool or DelegateTool or AgentResultTool or FollowupTool;

    public static Dictionary<string, OneWareTool> AddTools(Dictionary<string, OneWareTool> tools)
    {
        // Keep the MCP surface stable between model selection and prompts. A new Codex
        // session cannot be resumed before its first prompt has persisted it. Availability
        // is checked against the originating prompt's catalog, never this tool list.
        Add(AIFunctionFactory.Create((string name) => name, LoadSkillTool,
            "Load a registered OneWare plugin skill by name. Returns its instructions and resource directory."));
        Add(AIFunctionFactory.Create((string agent, string task, bool background = false) => task, DelegateTool,
            "Delegate to a registered OneWare plugin agent with inherited permissions and tool restrictions. " +
            "Set background=true to return a task id immediately and continue the parent conversation; " +
            "its completed result is delivered automatically. Otherwise wait for the result. " +
            "Both modes return a stable agent id for same-task follow-ups."));
        Add(AIFunctionFactory.Create((string id, string task, bool background = false) => task, FollowupTool,
            "Send additional instructions to an idle delegated agent by its returned id, retaining its conversation. " +
            "Use this for same-task follow-ups; create a new delegate for unrelated work. " +
            "If it is running, wait for its result first. background=true returns immediately and delivers completion automatically."));
        Add(AIFunctionFactory.Create((string id, string action = "status") => id, AgentResultTool,
            "Inspect the latest turn of a delegated agent owned by this conversation. action=status returns immediately, " +
            "action=wait waits for the result, and action=cancel stops that task and its descendants. " +
            "Do not repeatedly poll; continue useful work or let the user see progress in its agent block."));
        return tools;

        void Add(AIFunction function)
        {
            if (!tools.TryAdd(function.Name, new OneWareTool(function, true, null)))
                throw new InvalidOperationException("A plugin uses the reserved ACP tool name: " + function.Name);
        }
    }

    public string Describe() => Agents.Length == 0 && Skills.Length == 0 ? "" :
        "\nOneWare plugin catalog (names and descriptions, not loaded instructions):\n" +
        JsonSerializer.Serialize(new
        {
            skills = Skills.Select(s => new { s.Name, s.Description }),
            agents = Agents.Select(a => new { a.Name, a.DisplayName, a.Description })
        }) + "\nUse mcp__oneware__oneware_load_skill to load matching skills on demand. " +
        "Use mcp__oneware__oneware_delegate for matching specialized work. " +
        "Use background=true when work can proceed independently; completion is delivered automatically to this conversation. " +
        "Continue useful work, or say you are waiting and end your turn; do not poll. Results also remain available " +
        "in the agent block and through mcp__oneware__oneware_agent_result. " +
        "For additional instructions on the same task, use mcp__oneware__oneware_agent_followup with the returned id. " +
        "These tools preserve OneWare's approvals; do not use runtime-native skill or agent tools instead.";

    public static AcpPluginCatalog Read(IEnumerable<OneWareAiAgent> agents, IEnumerable<string> directories,
        string userRequest)
    {
        var skills = new Dictionary<string, AcpPluginSkill>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in directories)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
            {
                var file = Path.Combine(directory, "SKILL.md");
                if (!File.Exists(file)) continue;
                var skill = ReadSkill(file);
                if (skill != null) skills[skill.Name] = skill;
            }
        }

        return new AcpPluginCatalog(agents.Where(a => a.Infer || Named(userRequest, a.Name) ||
                (!string.IsNullOrWhiteSpace(a.DisplayName) && Named(userRequest, a.DisplayName)))
            .Select(a => new OneWareAiAgent
            {
                Name = a.Name, DisplayName = a.DisplayName, Description = a.Description,
                Instructions = a.Instructions, Model = a.Model, ReasoningEffort = a.ReasoningEffort,
                Tools = a.Tools?.ToArray(), Skills = a.Skills?.ToArray(), Infer = a.Infer
            }).ToArray(), skills.Values.ToArray());
    }

    private static bool Named(string request, string name) => Regex.IsMatch(request,
        @"(?<![\w-])" + Regex.Escape(name) + @"(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static AcpPluginSkill? ReadSkill(string file)
    {
        var text = File.ReadAllText(file).Replace("\r\n", "\n").TrimStart('\uFEFF');
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return null;
        var end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
        if (end < 0) return null;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < end; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line.StartsWith('#')) continue;
            var separator = line.IndexOf(':');
            if (separator < 1) continue;
            var name = line[..separator].Trim();
            if (name is not ("name" or "description")) continue;
            var value = line[(separator + 1)..].Trim();
            if (value.StartsWith('>') || value.StartsWith('|'))
            {
                var folded = value[0] == '>';
                var parts = new List<string>();
                while (i + 1 < end && (string.IsNullOrWhiteSpace(lines[i + 1]) || char.IsWhiteSpace(lines[i + 1][0])))
                    parts.Add(lines[++i].Trim());
                value = string.Join(folded ? " " : "\n", parts).Trim();
            }
            else if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                try { value = JsonSerializer.Deserialize<string>(value) ?? ""; }
                catch (JsonException) { value = value[1..^1]; }
            }
            else if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
                value = value[1..^1].Replace("''", "'");
            else if (value.IndexOf(" #", StringComparison.Ordinal) is var comment && comment >= 0)
                value = value[..comment].TrimEnd();
            fields[name] = value;
        }

        var body = string.Join("\n", lines.Skip(end + 1)).Trim();
        return fields.TryGetValue("name", out var skillName) && !string.IsNullOrWhiteSpace(skillName) &&
               fields.TryGetValue("description", out var description) && !string.IsNullOrWhiteSpace(description) &&
               body.Length > 0
            ? new AcpPluginSkill(skillName, description, body, Path.GetFullPath(Path.GetDirectoryName(file)!))
            : null;
    }
}

internal sealed record AcpPluginSkill(string Name, string Description, string Instructions, string Directory)
{
    public string Content => Instructions + "\n\nSkill resource directory: " + Directory +
                             "\nResolve relative resource paths against this directory and use OneWare tools to read them.";
}

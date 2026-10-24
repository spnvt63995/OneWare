using OneWare.Essentials.Models;
using OneWare.Essentials.Services;

namespace OneWare.TestPlugin;

/// <summary>Opt-in fixtures enabled by marker files in an isolated verification profile.</summary>
public sealed class AcpSkillsVerificationModule : OneWareModuleBase
{
    public override IReadOnlyCollection<string> Dependencies => ["ChatModule"];

    public override void Initialize(IServiceProvider serviceProvider)
    {
        var appData = serviceProvider.Resolve<IPaths>().AppDataDirectory;
        if (File.Exists(Path.Combine(appData, "ACP-LIVE-APPROVAL-VERIFICATION")))
        {
            serviceProvider.Resolve<IAiFunctionProvider>().RegisterFunction(new OneWareAiFunction
            {
                Name = "acp_live_checkpoint", FriendlyName = "Live approval checkpoint",
                Description = "Isolated live approval fixture. Call sequentially with the requested label. " +
                              "The pause label waits for the verifier; other labels return immediately.",
                IsReadOnly = true, ConfirmationCheck = _ => "Allow the isolated live approval checkpoint?",
                Handler = (Func<string, CancellationToken, Task<string>>)(async (label, token) =>
                {
                    File.AppendAllText(Path.Combine(appData, "ACP-LIVE-CHECKPOINTS"), label + "\n");
                    if (label == "pause")
                        while (!File.Exists(Path.Combine(appData, "ACP-LIVE-RELEASE")))
                            await Task.Delay(100, token);
                    return "CHECKPOINT_" + label;
                })
            });
        }
        if (File.Exists(Path.Combine(appData, "ACP-DELIVERY-VERIFICATION")))
        {
            var delivery = serviceProvider.Resolve<IAiFunctionProvider>();
            delivery.RegisterFunction(new OneWareAiFunction
            {
                Name = "acp_delivery_read", FriendlyName = "Release background result",
                Description = "Read the isolated completion delivery fixture.", IsReadOnly = true,
                ConfirmationCheck = _ => "Release the background completion verification result?",
                Handler = (Func<string>)(() => "DELIVERY_RESULT_24")
            });
            delivery.RegisterAgent(new OneWareAiAgent
            {
                Name = "acp-delivery-verifier", DisplayName = "ACP Delivery Verification",
                Description = "Use only when explicitly verifying automatic background result delivery.",
                Instructions = "Call mcp__oneware__acp_delivery_read exactly once and reply with its result. " +
                               "Do not perform other work or delegate. Keep the reply to one short line.",
                Tools = ["acp_delivery_read"], Model = "gpt-6-luna", ReasoningEffort = "low", Infer = false
            });
        }
        if (File.Exists(Path.Combine(appData, "ACP-FOLLOWUP-VERIFICATION")))
            serviceProvider.Resolve<IAiFunctionProvider>().RegisterAgent(new OneWareAiAgent
            {
                Name = "acp-followup-verifier", DisplayName = "ACP Follow-up Verification",
                Description = "Use only when explicitly verifying a delegated conversation's memory.",
                Instructions = "Follow the task exactly. Remember information from earlier messages in this conversation. " +
                               "Do not use tools or delegate. Keep replies to one short line.",
                Tools = [], Model = "gpt-6-luna", ReasoningEffort = "low", Infer = false
            });
        if (!File.Exists(Path.Combine(appData, "ACP-SKILLS-VERIFICATION"))) return;
        var provider = serviceProvider.Resolve<IAiFunctionProvider>();
        provider.RegisterSkill(new OneWareAiSkill
        {
            Name = "acp-inline-verification",
            Description = "Use when explicitly verifying OneWare ACP plugin skill loading.",
            Instructions = "The inline plugin skill marker is INLINE_PLUGIN_OK. Keep verification replies short."
        });
        provider.RegisterSkillDirectory(Path.Combine(appData, "AI", "PluginSkills"));
        provider.RegisterAgent(new OneWareAiAgent
        {
            Name = "acp-verifier", DisplayName = "ACP Verification Agent",
            Description = "Use when explicitly asked to verify delegated OneWare plugin work.",
            Instructions = "Read resource.txt from the preloaded skill directory using mcp__oneware__readFile. " +
                           "Do not use any other tools or delegate further. " +
                           "Reply briefly with CHILD_PLUGIN_OK followed by the file's marker.",
            Skills = ["acp-resource-verification"], Tools = ["readFile"],
            Model = "gpt-6-luna", ReasoningEffort = "low", Infer = false
        });
    }
}

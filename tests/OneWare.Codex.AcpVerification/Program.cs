using OneWare.Codex.UnitTests;

if (args.Length != 3 || args[2] is not ("probe" or "live" or "features" or "tools"))
    throw new ArgumentException("Expected: isolated-appdata evidence-directory probe|live|features|tools");
if (args[2] == "tools") await AcpToolRefreshLiveVerification.RunAsync(args[0], args[1]);
else if (args[2] == "features") await AcpFeatureLiveVerification.RunAsync(args[0], args[1]);
else await AcpLiveVerification.RunAsync(args[0], args[1], args[2] == "live");
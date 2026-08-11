using StrmAssistant.Compatibility;
using StrmAssistant.Experience;
using System.Runtime.CompilerServices;

namespace StrmAssistant.ContractTests;

internal static class OpenListDirectLinkSecurityContractTests
{
    [ModuleInitializer]
    internal static void RunModuleTests()
    {
        RemoteDeepDeleteRuntimeSettings.Save(new RemoteDeepDeleteOptions
        {
            Enabled = true,
            Provider = RemoteDeepDeleteProviderType.OpenList,
            BaseUrl = "https://openlist.example.com",
            AccessToken = "token",
            AllowedRemoteRoots = "/115",
            TimeoutSeconds = 5,
            TreatNotFoundAsSuccess = true
        });

        var plan = new RemoteDeepDeletePlan
        {
            Applicable = true,
            Allowed = false,
            TargetLooksRemote = true,
            Provider = RemoteDeepDeleteProviderType.OpenList.ToString(),
            SourceTarget = "http://openlist.example.com:443/d/115/movie.mkv",
            Error = "The resolved media target did not match any configured remote path mapping."
        };
        OpenListDirectLinkDeepDeletePatches.BuildPlanPostfix(null, ref plan);
        if (plan.Allowed)
            throw new InvalidOperationException(
                "OpenList same-origin fallback accepted an HTTP target against an HTTPS BaseUrl merely because host/port matched.");
        Console.WriteLine("[PASS] OpenList direct-link fallback requires same scheme/host/port origin");
    }
}

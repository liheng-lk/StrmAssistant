using StrmAssistant.Compatibility;
using StrmAssistant.Experience;
using System.Runtime.CompilerServices;

namespace StrmAssistant.ContractTests;

internal static class OpenListDirectLinkContractTests
{
    [ModuleInitializer]
    internal static void RunModuleTests()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("OpenList direct-link fallback maps same-origin /d path", MapsSameOriginDirectLink),
            ("OpenList direct-link fallback URL-decodes mount path", DecodesDirectLinkPath),
            ("OpenList direct-link fallback rejects different authority", RejectsAuthorityMismatch),
            ("OpenList direct-link fallback rejects non-d path", RejectsNonDirectLinkPath),
            ("OpenList direct-link fallback enforces AllowedRemoteRoots", EnforcesAllowedRoots),
            ("OpenList direct-link fallback does not override other plan failures", DoesNotOverrideOtherFailure),
            ("OpenList direct-link fallback requires AccessToken", RequiresAccessToken)
        };
        var failures = new List<string>();
        Console.WriteLine($"StrmAssistant OpenList direct-link contract tests: {tests.Length} cases");
        foreach (var test in tests)
        {
            try
            {
                test.Body();
                Console.WriteLine($"[PASS] {test.Name}");
            }
            catch (Exception ex)
            {
                failures.Add(test.Name + ": " + ex.GetBaseException().Message);
                Console.Error.WriteLine($"[FAIL] {test.Name}");
                Console.Error.WriteLine(ex.ToString());
            }
        }
        if (failures.Count > 0)
            throw new InvalidOperationException("OpenList direct-link contract failures: " + string.Join(" | ", failures));
    }

    private static void MapsSameOriginDirectLink()
    {
        Configure("/115", true);
        var plan = MissingMappingPlan("https://openlist.example.com/d/115/movie.mkv?sign=secret");
        OpenListDirectLinkDeepDeletePatches.BuildPlanPostfix(null, ref plan);
        AssertTrue(plan.Allowed && plan.Applicable, "Same-origin /d link was not auto-mapped: " + plan.Error);
        AssertEqual("/115/movie.mkv", plan.RemotePath, "Auto-mapped remote path mismatch.");
        AssertEqual("/115", plan.RemoteDirectory, "Auto-mapped directory mismatch.");
        AssertEqual("movie.mkv", plan.RemoteName, "Auto-mapped filename mismatch.");
        AssertEqual("[OpenList same-origin /d/ auto-map]", plan.MatchedSourcePrefix, "Fallback marker mismatch.");
    }

    private static void DecodesDirectLinkPath()
    {
        Configure("/115", true);
        var plan = MissingMappingPlan("https://openlist.example.com/d/115/%E7%94%B5%E5%BD%B1/movie.mkv");
        OpenListDirectLinkDeepDeletePatches.BuildPlanPostfix(null, ref plan);
        AssertTrue(plan.Allowed, "Encoded direct link was not mapped: " + plan.Error);
        AssertEqual("/115/电影/movie.mkv", plan.RemotePath, "Direct-link URL decoding mismatch.");
    }

    private static void RejectsAuthorityMismatch()
    {
        Configure("/115", true);
        var plan = MissingMappingPlan("https://alias.example.com/d/115/movie.mkv");
        OpenListDirectLinkDeepDeletePatches.BuildPlanPostfix(null, ref plan);
        AssertFalse(plan.Allowed, "Different authority was auto-mapped.");
        AssertTrue(plan.RemotePath == null, "Rejected authority still produced a remote path.");
    }

    private static void RejectsNonDirectLinkPath()
    {
        Configure("/115", true);
        var plan = MissingMappingPlan("https://openlist.example.com/raw/115/movie.mkv");
        OpenListDirectLinkDeepDeletePatches.BuildPlanPostfix(null, ref plan);
        AssertFalse(plan.Allowed, "Non-/d/ path was auto-mapped.");
    }

    private static void EnforcesAllowedRoots()
    {
        Configure("/safe", true);
        var plan = MissingMappingPlan("https://openlist.example.com/d/115/movie.mkv");
        OpenListDirectLinkDeepDeletePatches.BuildPlanPostfix(null, ref plan);
        AssertFalse(plan.Allowed, "Direct-link path outside AllowedRemoteRoots was allowed.");
        AssertTrue(plan.RemotePath == null, "Blocked plan should not be rewritten into an allowed remote path.");
    }

    private static void DoesNotOverrideOtherFailure()
    {
        Configure("/115", true);
        var plan = MissingMappingPlan("https://openlist.example.com/d/115/movie.mkv");
        plan.Error = "OpenList AccessToken is empty.";
        OpenListDirectLinkDeepDeletePatches.BuildPlanPostfix(null, ref plan);
        AssertFalse(plan.Allowed, "Fallback overrode a non-mapping plan failure.");
        AssertEqual("OpenList AccessToken is empty.", plan.Error, "Fallback rewrote unrelated error.");
    }

    private static void RequiresAccessToken()
    {
        Configure("/115", false);
        var plan = MissingMappingPlan("https://openlist.example.com/d/115/movie.mkv");
        OpenListDirectLinkDeepDeletePatches.BuildPlanPostfix(null, ref plan);
        AssertFalse(plan.Allowed, "Direct-link fallback allowed anonymous destructive access.");
    }

    private static RemoteDeepDeletePlan MissingMappingPlan(string sourceTarget)
    {
        return new RemoteDeepDeletePlan
        {
            Applicable = true,
            Allowed = false,
            TargetLooksRemote = true,
            Provider = RemoteDeepDeleteProviderType.OpenList.ToString(),
            SourceTarget = sourceTarget,
            Error = "The resolved media target did not match any configured remote path mapping."
        };
    }

    private static void Configure(string allowedRoots, bool withToken)
    {
        RemoteDeepDeleteRuntimeSettings.Save(new RemoteDeepDeleteOptions
        {
            Enabled = true,
            Provider = RemoteDeepDeleteProviderType.OpenList,
            BaseUrl = "https://openlist.example.com",
            AccessToken = withToken ? "token" : string.Empty,
            AllowedRemoteRoots = allowedRoots,
            TimeoutSeconds = 5,
            TreatNotFoundAsSuccess = true
        });
    }

    private static void AssertTrue(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void AssertFalse(bool value, string message) => AssertTrue(!value, message);

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(message + " Expected=" + expected + ", Actual=" + actual);
    }
}

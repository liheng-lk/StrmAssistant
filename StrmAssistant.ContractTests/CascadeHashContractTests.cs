using StrmAssistant.Api;
using StrmAssistant.Experience;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace StrmAssistant.ContractTests;

internal static class CascadeHashContractTests
{
    [ModuleInitializer]
    internal static void RunModuleTests()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("Cascade PlanHash is stable for identical plan", HashIsStable),
            ("Cascade PlanHash changes when remote path changes", HashChangesWithRemotePath),
            ("Cascade PlanHash changes when allow decision changes", HashChangesWithAllowedFlag),
            ("Cascade PlanHash changes when local delete entry changes", HashChangesWithLocalEntry),
            ("Cascade PlanHash changes when remote allow roots change", HashChangesWithRemoteConfiguration),
            ("Cascade fixed comparison accepts exact and rejects changed hash", FixedComparisonWorks)
        };
        var failures = new List<string>();
        Console.WriteLine($"StrmAssistant cascade hash contract tests: {tests.Length} cases");
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
            throw new InvalidOperationException("Cascade hash contract failures: " + string.Join(" | ", failures));
    }

    private static void HashIsStable()
    {
        Configure("/115");
        var plan = SamplePlan();
        var first = Hash(plan);
        var second = Hash(plan);
        AssertEqual(first, second, "Identical plan produced a different hash.");
        AssertTrue(first.Length == 64, "SHA-256 plan hash should be 64 hex characters.");
    }

    private static void HashChangesWithRemotePath()
    {
        Configure("/115");
        var first = SamplePlan();
        var second = SamplePlan();
        second.Entries[0].RemotePlan.RemotePath = "/115/other.mkv";
        AssertNotEqual(Hash(first), Hash(second), "Remote path mutation did not invalidate PlanHash.");
    }

    private static void HashChangesWithAllowedFlag()
    {
        Configure("/115");
        var first = SamplePlan();
        var second = SamplePlan();
        second.Entries[0].Allowed = false;
        AssertNotEqual(Hash(first), Hash(second), "Allow/deny mutation did not invalidate PlanHash.");
    }

    private static void HashChangesWithLocalEntry()
    {
        Configure("/115");
        var first = SamplePlan();
        var second = SamplePlan();
        second.Entries[1].LocalPlan.Entries[0].Path = "C:/allowed/changed.nfo";
        AssertNotEqual(Hash(first), Hash(second), "Local destructive path mutation did not invalidate PlanHash.");
    }

    private static void HashChangesWithRemoteConfiguration()
    {
        Configure("/115");
        var plan = SamplePlan();
        var first = Hash(plan);
        Configure("/other-root");
        var second = Hash(plan);
        AssertNotEqual(first, second, "Remote allowed-root configuration mutation did not invalidate PlanHash.");
    }

    private static void FixedComparisonWorks()
    {
        var method = typeof(RemoteDeepDeleteCascadeApiService).GetMethod("FixedEquals",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (method == null) throw new InvalidOperationException("FixedEquals was not found.");
        AssertTrue((bool)method.Invoke(null, new object[] { "abcdef", "abcdef" }), "Equal hash was rejected.");
        AssertFalse((bool)method.Invoke(null, new object[] { "abcdef", "abcdeg" }), "Changed hash was accepted.");
        AssertFalse((bool)method.Invoke(null, new object[] { "abcdef", "abc" }), "Different-length hash was accepted.");
    }

    private static RemoteDeepDeleteCascadePlan SamplePlan()
    {
        return new RemoteDeepDeleteCascadePlan
        {
            Applicable = true,
            Allowed = true,
            Entries = new List<RemoteDeepDeleteCascadeEntry>
            {
                new RemoteDeepDeleteCascadeEntry
                {
                    ItemId = "100",
                    ItemPath = "C:/library/movie.strm",
                    LooksRemote = true,
                    RequiresRemoteDelete = true,
                    Allowed = true,
                    RemotePlan = new RemoteDeepDeletePlan
                    {
                        Provider = "OpenList",
                        RemotePath = "/115/movie.mkv"
                    }
                },
                new RemoteDeepDeleteCascadeEntry
                {
                    ItemId = "101",
                    ItemPath = "C:/library/local.strm",
                    RequiresLocalDeepDelete = true,
                    LocalDeepDeleteAllowed = true,
                    LocalPlan = LocalPlan("C:/allowed/local.mkv")
                }
            }
        };
    }

    private static DeepDeletePlan LocalPlan(string path)
    {
        var plan = new DeepDeletePlan { SourcePath = "C:/library/local.strm" };
        plan.Entries.Add(new DeepDeletePlanEntry
        {
            Path = path,
            Kind = DeepDeleteEntryKind.StrmTarget,
            Allowed = true,
            Reason = "contract"
        });
        return plan;
    }

    private static string Hash(RemoteDeepDeleteCascadePlan plan)
    {
        var method = typeof(RemoteDeepDeleteCascadeApiService).GetMethod("ComputePlanHash",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (method == null) throw new InvalidOperationException("ComputePlanHash was not found.");
        return (string)method.Invoke(null, new object[]
        {
            Array.Empty<MediaBrowser.Controller.Entities.BaseItem>(), plan
        });
    }

    private static void Configure(string allowedRoots)
    {
        RemoteDeepDeleteRuntimeSettings.Save(new RemoteDeepDeleteOptions
        {
            Enabled = true,
            Provider = RemoteDeepDeleteProviderType.OpenList,
            BaseUrl = "https://openlist.example.com",
            AccessToken = "token",
            PathMappings = "https://cdn.example.com/d/115 => /115",
            AllowedRemoteRoots = allowedRoots,
            TimeoutSeconds = 5,
            TreatNotFoundAsSuccess = true,
            DeleteAssociatedSidecars = true
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

    private static void AssertNotEqual<T>(T left, T right, string message)
    {
        if (EqualityComparer<T>.Default.Equals(left, right))
            throw new InvalidOperationException(message + " Value=" + left);
    }
}

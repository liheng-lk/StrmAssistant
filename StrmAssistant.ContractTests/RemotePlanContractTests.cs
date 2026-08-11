using MediaBrowser.Controller.Entities;
using StrmAssistant.Experience;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace StrmAssistant.ContractTests;

internal static class RemotePlanContractTests
{
    [ModuleInitializer]
    internal static void RunModuleTests()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("Remote BuildPlan reads real STRM and maps decoded child path", BuildPlanMapsRealStrm),
            ("Remote BuildPlan redacts signed query from source identity", BuildPlanRedactsSignedQuery),
            ("Remote BuildPlan rejects textual path-prefix collision", BuildPlanRejectsPrefixCollision),
            ("Remote BuildPlan rejects authority mismatch", BuildPlanRejectsAuthorityMismatch),
            ("Remote BuildPlan enforces allowed remote root", BuildPlanEnforcesAllowedRoot)
        };
        var failures = new List<string>();
        Console.WriteLine($"StrmAssistant remote plan contract tests: {tests.Length} cases");
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
            throw new InvalidOperationException("Remote plan contract failures: " + string.Join(" | ", failures));
    }

    private static void BuildPlanMapsRealStrm()
    {
        WithStrm("https://alist.example.com/d/115/%E7%94%B5%E5%BD%B1/movie.mkv?sign=abc#frag", item =>
        {
            Configure("https://alist.example.com/d/115 => /115", "/115");
            var plan = new RemoteDeepDeleteService().BuildPlan(item);
            AssertTrue(plan.Applicable && plan.Allowed, "Mapped STRM plan is not allowed: " + plan.Error);
            AssertEqual("/115/电影/movie.mkv", plan.RemotePath, "Decoded remote path mismatch.");
            AssertEqual("/115/电影", plan.RemoteDirectory, "Remote directory mismatch.");
            AssertEqual("movie.mkv", plan.RemoteName, "Remote filename mismatch.");
        });
    }

    private static void BuildPlanRedactsSignedQuery()
    {
        WithStrm("https://alist.example.com/d/115/movie.mkv?token=super-secret&expires=999#private", item =>
        {
            Configure("https://alist.example.com/d/115 => /115", "/115");
            var plan = new RemoteDeepDeleteService().BuildPlan(item);
            AssertTrue(plan.Allowed, "Plan unexpectedly blocked: " + plan.Error);
            AssertFalse(plan.SourceTarget.Contains("super-secret", StringComparison.Ordinal),
                "Signed query leaked into SourceTarget.");
            AssertFalse(plan.SourceTarget.Contains("?", StringComparison.Ordinal), "Query delimiter was not redacted.");
            AssertFalse(plan.SourceTarget.Contains("#", StringComparison.Ordinal), "Fragment delimiter was not redacted.");
        });
    }

    private static void BuildPlanRejectsPrefixCollision()
    {
        WithStrm("https://alist.example.com/d/115abc/movie.mkv", item =>
        {
            Configure("https://alist.example.com/d/115 => /115", "/115");
            var plan = new RemoteDeepDeleteService().BuildPlan(item);
            AssertTrue(plan.Applicable, "Remote URL should still be recognized as remote.");
            AssertFalse(plan.Allowed, "Textual prefix collision was mapped and allowed.");
            AssertContains(plan.Error, "did not match", "Expected mapping rejection reason.");
        });
    }

    private static void BuildPlanRejectsAuthorityMismatch()
    {
        WithStrm("https://evil.example.com/d/115/movie.mkv", item =>
        {
            Configure("https://alist.example.com/d/115 => /115", "/115");
            var plan = new RemoteDeepDeleteService().BuildPlan(item);
            AssertFalse(plan.Allowed, "Different authority matched configured mapping.");
            AssertContains(plan.Error, "did not match", "Expected authority mismatch to fail mapping.");
        });
    }

    private static void BuildPlanEnforcesAllowedRoot()
    {
        WithStrm("https://alist.example.com/d/115/movie.mkv", item =>
        {
            Configure("https://alist.example.com/d/115 => /115", "/safe");
            var plan = new RemoteDeepDeleteService().BuildPlan(item);
            AssertTrue(plan.Applicable, "Mapped remote target should be applicable before allow-list decision.");
            AssertFalse(plan.Allowed, "Remote path outside allowed root was allowed.");
            AssertContains(plan.Error, "outside", "Expected allow-root rejection reason.");
        });
    }

    private static void Configure(string mappings, string allowedRoots)
    {
        RemoteDeepDeleteRuntimeSettings.Save(new RemoteDeepDeleteOptions
        {
            Enabled = true,
            Provider = RemoteDeepDeleteProviderType.OpenList,
            BaseUrl = "https://openlist.example.com",
            AccessToken = "contract-token",
            PathMappings = mappings,
            AllowedRemoteRoots = allowedRoots,
            TimeoutSeconds = 5,
            TreatNotFoundAsSuccess = true
        });
    }

    private static void WithStrm(string target, Action<BaseItem> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "strmassistant-remote-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "movie.strm");
            File.WriteAllText(path, target + Environment.NewLine);
            action(CreateVideoItem(path));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static BaseItem CreateVideoItem(string path)
    {
        var assembly = typeof(BaseItem).Assembly;
        foreach (var name in new[]
        {
            "MediaBrowser.Controller.Entities.Movies.Movie",
            "MediaBrowser.Controller.Entities.TV.Episode"
        })
        {
            var type = assembly.GetType(name, false);
            if (type == null || type.IsAbstract) continue;
            var item = (BaseItem)Activator.CreateInstance(type, nonPublic: true);
            SetProperty(item, "Path", path);
            SetProperty(item, "Name", "contract movie");
            return item;
        }
        throw new InvalidOperationException("No concrete video item type available.");
    }

    private static void SetProperty(object target, string name, object value)
    {
        var property = target.GetType().GetProperty(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        var setter = property?.GetSetMethod(true);
        if (setter == null) throw new InvalidOperationException("Property is not writable: " + name);
        setter.Invoke(target, new[] { value });
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

    private static void AssertContains(string value, string expected, string message)
    {
        if (value?.Contains(expected, StringComparison.OrdinalIgnoreCase) != true)
            throw new InvalidOperationException(message + " Actual=" + value);
    }
}

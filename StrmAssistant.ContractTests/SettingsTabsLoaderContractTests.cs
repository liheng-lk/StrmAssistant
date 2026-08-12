using StrmAssistant.Web.Service;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace StrmAssistant.ContractTests;

internal static class SettingsTabsLoaderContractTests
{
    [ModuleInitializer]
    internal static void RunModuleTests()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("Settings tabs module name is plugin-version cache busted", ModuleNameIsVersioned),
            ("Settings tabs module name sanitizes route-unsafe version characters", ModuleNameSanitizesVersion),
            ("Settings tabs bootstrap retries async module loading instead of failing once", LoaderRetries),
            ("Settings tabs embedded JavaScript resource is present and non-empty", EmbeddedResourceExists)
        };

        var failures = new List<string>();
        Console.WriteLine($"StrmAssistant settings-tabs loader contract tests: {tests.Length} cases");
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
            throw new InvalidOperationException("settings-tabs loader contract failures: " + string.Join(" | ", failures));
    }

    private static void ModuleNameIsVersioned()
    {
        var first = ShortcutMenuService.BuildSettingsTabsModuleName("2.0.7.0");
        var second = ShortcutMenuService.BuildSettingsTabsModuleName("2.0.8.0");

        AssertEqual("components/strmassistant/settings-tabs-v2_0_7_0", first,
            "Unexpected module name for 2.0.7.0.");
        AssertEqual("components/strmassistant/settings-tabs-v2_0_8_0", second,
            "Unexpected module name for 2.0.8.0.");
        AssertTrue(!string.Equals(first, second, StringComparison.Ordinal),
            "Different plugin versions must not reuse the same AMD module/cache key.");
    }

    private static void ModuleNameSanitizesVersion()
    {
        var value = ShortcutMenuService.BuildSettingsTabsModuleName("2.0.8-beta+test");
        AssertEqual("components/strmassistant/settings-tabs-v2_0_8_beta_test", value,
            "Version characters were not normalized into a safe route segment.");
    }

    private static void LoaderRetries()
    {
        var loader = ShortcutMenuService.BuildSettingsTabsLoader("2.0.8.0");
        AssertContains(loader, "components/strmassistant/settings-tabs-v2_0_8_0",
            "Loader does not request the versioned module.");
        AssertContains(loader, "maxAttempts = 40",
            "Loader no longer has the bounded retry contract.");
        AssertContains(loader, ".catch(() => retry())",
            "Rejected AMD loads are not retried.");
        AssertContains(loader, "__strmAssistantSettingsTabsLoaded = true",
            "Loader does not expose a runtime success marker for browser verification.");
    }

    private static void EmbeddedResourceExists()
    {
        var assembly = typeof(StrmAssistant.Plugin).GetTypeInfo().Assembly;
        const string resourceName = "StrmAssistant.Web.Resources.settings-tabs.js";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        AssertTrue(stream != null, "settings-tabs.js is not embedded in the plugin assembly.");
        AssertTrue(stream.Length > 512, "Embedded settings-tabs.js is unexpectedly empty/truncated.");
    }

    private static void AssertTrue(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(message + " Expected=" + expected + ", Actual=" + actual);
    }

    private static void AssertContains(string value, string expected, string message)
    {
        if (value?.Contains(expected, StringComparison.Ordinal) != true)
            throw new InvalidOperationException(message + " Actual=" + value);
    }
}

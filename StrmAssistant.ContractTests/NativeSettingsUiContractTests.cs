using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using StrmAssistant.UI;
using System.Runtime.CompilerServices;

namespace StrmAssistant.ContractTests;

internal static class NativeSettingsUiContractTests
{
    [ModuleInitializer]
    internal static void RunModuleTests()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("Native settings controller uses Emby IHasTabbedUIPages", UsesNativeTabbedInterface),
            ("Native settings exposes exactly six functional tabs", HasSixTabs),
            ("Native settings tab labels match requested categories", TabLabelsMatch),
            ("Native settings page is the main config page", IsMainConfigPage),
            ("Legacy DOM settings tabs module is explicitly retired", LegacyDomModuleRetired),
        };

        var failures = new List<string>();
        Console.WriteLine($"StrmAssistant native settings UI contract tests: {tests.Length} cases");
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
            throw new InvalidOperationException("native settings UI contract failures: " + string.Join(" | ", failures));
    }

    private static NativeSettingsMainController CreateController()
    {
        var info = new PluginInfo { Id = "63c322b7-a371-41a3-b11f-04f8418b37d8" };
        return new NativeSettingsMainController(info, null);
    }

    private static void UsesNativeTabbedInterface()
    {
        AssertTrue(typeof(IHasTabbedUIPages).IsAssignableFrom(typeof(NativeSettingsMainController)),
            "Controller does not implement Emby's native IHasTabbedUIPages contract.");
    }

    private static void HasSixTabs()
    {
        var controller = CreateController();
        AssertEqual(6, controller.TabPageControllers.Count, "Native settings tab count mismatch.");
    }

    private static void TabLabelsMatch()
    {
        var controller = CreateController();
        var labels = controller.TabPageControllers.Select(page => page.PageInfo.DisplayName).ToArray();
        var expected = new[] { "常规", "媒体信息", "元数据", "片头片尾", "体验增强", "关于" };
        AssertEqual(string.Join("|", expected), string.Join("|", labels), "Native tab order/labels changed.");
    }

    private static void IsMainConfigPage()
    {
        var controller = CreateController();
        AssertTrue(controller.PageInfo.IsMainConfigPage, "Native settings controller is not marked as main config page.");
        AssertTrue(controller.PageInfo.EnableInMainMenu, "Native settings controller is not enabled in plugin UI navigation.");
    }

    private static void LegacyDomModuleRetired()
    {
        var assembly = typeof(StrmAssistant.Plugin).Assembly;
        using var stream = assembly.GetManifestResourceStream("StrmAssistant.Web.Resources.settings-tabs.js");
        AssertTrue(stream != null, "Compatibility settings-tabs.js resource is missing.");
        using var reader = new StreamReader(stream!);
        var script = reader.ReadToEnd();
        AssertTrue(script.Contains("native-emby-plugin-ui", StringComparison.Ordinal),
            "Legacy settings-tabs endpoint is not a native-UI compatibility no-op.");
        AssertTrue(!script.Contains("MutationObserver", StringComparison.Ordinal),
            "Legacy settings-tabs resource still performs DOM observation/mutation.");
        AssertTrue(!script.Contains("strmassistant-settings-section-hidden", StringComparison.Ordinal),
            "Legacy settings-tabs resource still hides GenericUI sections.");
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
}

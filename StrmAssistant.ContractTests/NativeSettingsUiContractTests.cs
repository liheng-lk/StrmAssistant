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
            ("Native settings exposes one default plus five additional tabs", HasSixVisibleTabs),
            ("Native settings tab labels match requested categories", TabLabelsMatch),
            ("Native tab host is not forced through the single-page main-config route", IsNotMainConfigPage),
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
        // Page factories are intentionally not invoked in this metadata contract. Live page creation/save is
        // verified in Emby runtime because it requires the real Plugin + IJsonSerializer services.
        return new NativeSettingsMainController(info, null, null);
    }

    private static void UsesNativeTabbedInterface()
    {
        AssertTrue(typeof(IHasTabbedUIPages).IsAssignableFrom(typeof(NativeSettingsMainController)),
            "Controller does not implement Emby's native IHasTabbedUIPages contract.");
    }

    private static void HasSixVisibleTabs()
    {
        var controller = CreateController();
        AssertEqual(5, controller.TabPageControllers.Count,
            "Emby tab host should receive only five additional page controllers; the default view is the first tab.");
        AssertEqual(6, controller.VisibleTabCount, "Native settings visible tab count mismatch.");
    }

    private static void TabLabelsMatch()
    {
        var controller = CreateController();
        AssertEqual("常规", controller.DefaultTabDisplayName, "Default tab label changed.");
        var labels = controller.TabPageControllers.Select(page => page.PageInfo.DisplayName).ToArray();
        var expected = new[] { "媒体信息", "元数据", "片头片尾", "体验增强", "关于" };
        AssertEqual(string.Join("|", expected), string.Join("|", labels), "Additional native tab order/labels changed.");
    }

    private static void IsNotMainConfigPage()
    {
        var controller = CreateController();
        AssertTrue(!controller.PageInfo.IsMainConfigPage,
            "Tabbed host must not be forced through Emby's single-page main-config route.");
        AssertTrue(controller.PageInfo.EnableInMainMenu,
            "Native tabbed settings controller is not enabled in plugin UI navigation.");
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

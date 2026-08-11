using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Notifications;
using StrmAssistant.Common;
using StrmAssistant.Notification;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

namespace StrmAssistant.ContractTests;

internal static class DeepDeleteNotificationDispatchContractTests
{
    [ModuleInitializer]
    internal static void RunModuleTests()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("deep.delete calls INotificationManager exactly once", SendsExactlyOnce),
            ("deep.delete keeps stable event id and legacy Mount Paths description", KeepsLegacyContract),
            ("deep.delete does not require notification enhancement options to build/send request", DoesNotReadEnhancementOptions)
        };

        var failures = new List<string>();
        Console.WriteLine($"StrmAssistant deep.delete dispatch contract tests: {tests.Length} cases");
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
            throw new InvalidOperationException("deep.delete dispatch contract failures: " + string.Join(" | ", failures));
    }

    private static void SendsExactlyOnce()
    {
        var harness = CreateHarness();
        const string target = "https://generic-cloud.example/video.mkv?token=keep";
        harness.Api.DeepDeleteSendNotification(CreateItem(), null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target });

        AssertEqual(1, harness.Proxy.SendCount, "INotificationManager.SendNotification call count mismatch.");
        AssertTrue(harness.Proxy.LastRequest != null, "Notification request was not captured.");
    }

    private static void KeepsLegacyContract()
    {
        var harness = CreateHarness();
        const string target = "https://another-provider.example/path/movie.mkv?signature=abc";
        var item = CreateItem();
        harness.Api.DeepDeleteSendNotification(item, null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target });

        var request = harness.Proxy.LastRequest ?? throw new InvalidOperationException("No notification request captured.");
        AssertEqual(StrmAssistantNotificationTypes.DeepDelete,
            Convert.ToString(GetProperty(request, "EventId")), "EventId changed.");

        var description = Convert.ToString(GetProperty(request, "Description")) ?? string.Empty;
        AssertContains(description, "Item Name:", "Legacy Item Name field is missing.");
        AssertContains(description, item.Name, "Item name is missing from description.");
        AssertContains(description, "Item Path:", "Legacy Item Path field is missing.");
        AssertContains(description, item.Path, "Item path is missing from description.");
        AssertContains(description, "Mount Paths:", "Legacy Mount Paths field is missing.");
        AssertContains(description, target, "Raw provider-agnostic STRM target is missing from Mount Paths.");
    }

    private static void DoesNotReadEnhancementOptions()
    {
        // The object is intentionally created without Plugin.Instance. If DeepDeleteSendNotification
        // touches ExperienceOptions/Plugin.Instance, this invocation throws and the contract fails.
        var harness = CreateHarness();
        harness.Api.DeepDeleteSendNotification(CreateItem(), null,
            new HashSet<string> { "https://provider.example/item.mkv" });
        AssertEqual(1, harness.Proxy.SendCount,
            "deep.delete was gated on plugin notification-enhancement state.");
    }

    private static Movie CreateItem()
    {
        return new Movie
        {
            Name = "Webhook Contract Movie",
            Path = Path.Combine(Path.GetTempPath(), "Webhook Contract Movie.strm")
        };
    }

    private static Harness CreateHarness()
    {
#pragma warning disable SYSLIB0050
        var api = (NotificationApi)FormatterServices.GetUninitializedObject(typeof(NotificationApi));
#pragma warning restore SYSLIB0050
        var manager = DispatchProxy.Create<INotificationManager, NotificationManagerProxy>();
        var proxy = (NotificationManagerProxy)(object)manager;

        var managerField = typeof(NotificationApi).GetField("_notificationManager",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (managerField == null) throw new InvalidOperationException("NotificationApi._notificationManager was not found.");
        managerField.SetValue(api, manager);

        return new Harness(api, proxy);
    }

    private static object GetProperty(object value, string name)
    {
        return value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(value);
    }

    private sealed class NotificationManagerProxy : DispatchProxy
    {
        public int SendCount { get; private set; }
        public object LastRequest { get; private set; }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod == null) throw new InvalidOperationException("Missing notification-manager method.");

            if (targetMethod.Name == "SendNotification")
            {
                SendCount++;
                LastRequest = args?.FirstOrDefault();
            }

            if (targetMethod.ReturnType == typeof(void)) return null;
            if (targetMethod.ReturnType == typeof(Task)) return Task.CompletedTask;
            if (targetMethod.ReturnType.IsValueType) return Activator.CreateInstance(targetMethod.ReturnType);
            return null;
        }
    }

    private sealed class Harness
    {
        public Harness(NotificationApi api, NotificationManagerProxy proxy)
        {
            Api = api;
            Proxy = proxy;
        }

        public NotificationApi Api { get; }
        public NotificationManagerProxy Proxy { get; }
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

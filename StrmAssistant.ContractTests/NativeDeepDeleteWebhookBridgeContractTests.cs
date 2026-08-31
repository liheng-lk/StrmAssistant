using MediaBrowser.Model.Services;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace StrmAssistant.ContractTests;

internal static class NativeDeepDeleteWebhookBridgeContractTests
{
    [ModuleInitializer]
    internal static void RunModuleTests()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("native deep.delete bridge finds DELETE /Items/{Id} outside LibraryService", FindsDeleteRouteOutsideLibraryService),
            ("native deep.delete bridge accepts handler with additional runtime arguments", AcceptsAdditionalHandlerArguments),
            ("native deep.delete bridge rejects unrelated route", RejectsUnrelatedRoute)
        };

        var failures = new List<string>();
        Console.WriteLine($"StrmAssistant native deep.delete webhook bridge contract tests: {tests.Length} cases");
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
            throw new InvalidOperationException("native deep.delete webhook bridge contract failures: " +
                                                string.Join(" | ", failures));
    }

    private static void FindsDeleteRouteOutsideLibraryService()
    {
        var method = typeof(RenamedDeleteController).GetMethod(nameof(RenamedDeleteController.Delete));
        AssertTrue(IsDeleteMethod(method), "DELETE /Items/{Id} was not discovered when service class was renamed.");
    }

    private static void AcceptsAdditionalHandlerArguments()
    {
        var method = typeof(RenamedDeleteController).GetMethod(nameof(RenamedDeleteController.DeleteWithToken));
        AssertTrue(IsDeleteMethod(method), "Handler with request + CancellationToken was rejected.");
    }

    private static void RejectsUnrelatedRoute()
    {
        var method = typeof(RenamedDeleteController).GetMethod(nameof(RenamedDeleteController.Other));
        AssertFalse(IsDeleteMethod(method), "Unrelated API route was incorrectly treated as item deletion.");
    }

    private static bool IsDeleteMethod(MethodInfo method)
    {
        var type = typeof(StrmAssistant.Experience.DeepDeleteService).Assembly.GetType(
            "StrmAssistant.Compatibility.NativeItemDeleteWebhookBridgeEntryPoint", true);
        var matcher = type.GetMethod("IsExplicitSingleItemDeleteMethod",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (matcher == null) throw new InvalidOperationException("Delete route matcher was not found.");
        return (bool)matcher.Invoke(null, new object[] { method });
    }

    private static void AssertTrue(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void AssertFalse(bool value, string message) => AssertTrue(!value, message);

    [Route("/Items/{Id}", "DELETE")]
    private sealed class FakeDeleteItem
    {
        public string Id { get; set; }
    }

    [Route("/Items/{Id}/Refresh", "POST")]
    private sealed class FakeOtherRequest
    {
        public string Id { get; set; }
    }

    private sealed class RenamedDeleteController
    {
        public void Delete(FakeDeleteItem request)
        {
        }

        public void DeleteWithToken(FakeDeleteItem request, CancellationToken cancellationToken)
        {
        }

        public void Other(FakeOtherRequest request)
        {
        }
    }
}

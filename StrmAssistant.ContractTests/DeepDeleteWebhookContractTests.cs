using StrmAssistant.Experience;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace StrmAssistant.ContractTests;

internal static class DeepDeleteWebhookContractTests
{
    [ModuleInitializer]
    internal static void RunModuleTests()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("deep.delete captures arbitrary signed HTTPS STRM target unchanged", CapturesArbitrarySignedHttpsTarget),
            ("deep.delete target capture is not OpenList-specific", CapturesNonOpenListProvider),
            ("deep.delete keeps raw STRM target plus provider mapped path", KeepsRawAndMappedTarget),
            ("deep.delete uses first non-empty STRM line", UsesFirstNonEmptyLine),
            ("deep.delete non-STRM source only uses explicit targets", NonStrmUsesExplicitTargetsOnly),
            ("deep.delete HTTP target detection accepts http and https", DetectsHttpTargets),
            ("deep.delete HTTP target detection rejects local paths", RejectsLocalAsHttpTarget)
        };

        var failures = new List<string>();
        Console.WriteLine($"StrmAssistant deep.delete webhook contract tests: {tests.Length} cases");
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
            throw new InvalidOperationException("deep.delete webhook contract failures: " + string.Join(" | ", failures));
    }

    private static void CapturesArbitrarySignedHttpsTarget()
    {
        const string target = "https://cdn.example.net/media/movie.mkv?token=abc123&expires=999999#stream";
        WithStrm("\r\n  " + target + "  \r\n", path =>
        {
            var values = Capture(path);
            AssertTrue(values.SetEquals(new[] { target }), "Signed URL was changed or not captured exactly.");
        });
    }

    private static void CapturesNonOpenListProvider()
    {
        const string target = "https://quark-proxy.example.org/share/xyz/video.mp4?sign=keep-me";
        WithStrm(target, path =>
        {
            var values = Capture(path);
            AssertTrue(values.Contains(target), "Generic/non-OpenList HTTPS target was not captured.");
        });
    }

    private static void KeepsRawAndMappedTarget()
    {
        const string raw = "https://alist.example.com/d/115/Movies/Test.mkv?sign=raw";
        const string mapped = "/115/Movies/Test.mkv";
        WithStrm(raw, path =>
        {
            var values = Capture(path, new[] { mapped });
            AssertTrue(values.Contains(raw), "Raw STRM direct link is missing.");
            AssertTrue(values.Contains(mapped), "Provider mapped path is missing.");
            AssertEqual(2, values.Count, "Unexpected notification target count.");
        });
    }

    private static void UsesFirstNonEmptyLine()
    {
        const string first = "https://one.example/file.mkv?token=1";
        const string second = "https://two.example/should-not-be-used.mkv";
        WithStrm("\n \n" + first + "\n" + second + "\n", path =>
        {
            var values = Capture(path);
            AssertTrue(values.SetEquals(new[] { first }), "STRM capture did not follow first non-empty-line semantics.");
        });
    }

    private static void NonStrmUsesExplicitTargetsOnly()
    {
        var path = Path.Combine(Path.GetTempPath(), "strmassistant-notification-" + Guid.NewGuid().ToString("N") + ".mkv");
        var values = Capture(path, new[] { "/mapped/only.mkv" });
        AssertTrue(values.SetEquals(new[] { "/mapped/only.mkv" }), "Non-STRM source invented a target.");
    }

    private static void DetectsHttpTargets()
    {
        AssertTrue(ContainsHttp(new[] { "http://example/file.mkv" }), "HTTP target was not detected.");
        AssertTrue(ContainsHttp(new[] { "https://example/file.mkv" }), "HTTPS target was not detected.");
    }

    private static void RejectsLocalAsHttpTarget()
    {
        AssertFalse(ContainsHttp(new[] { "/mnt/media/file.mkv", "C:\\Media\\file.mkv" }),
            "Local path was incorrectly classified as HTTP target.");
    }

    private static HashSet<string> Capture(string sourcePath, IEnumerable<string> extras = null)
    {
        var type = typeof(DeepDeleteService).Assembly.GetType("StrmAssistant.Experience.DeepDeleteNotificationTargets", true);
        var method = type.GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic);
        if (method == null) throw new InvalidOperationException("Missing production Capture method.");
        return (HashSet<string>)method.Invoke(null, new object[] { sourcePath, extras });
    }

    private static bool ContainsHttp(IEnumerable<string> values)
    {
        var type = typeof(DeepDeleteService).Assembly.GetType("StrmAssistant.Experience.DeepDeleteNotificationTargets", true);
        var method = type.GetMethod("ContainsHttpTarget", BindingFlags.Static | BindingFlags.NonPublic);
        if (method == null) throw new InvalidOperationException("Missing production ContainsHttpTarget method.");
        return (bool)method.Invoke(null, new object[] { values });
    }

    private static void WithStrm(string content, Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "strmassistant-webhook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "item.strm");
        File.WriteAllText(path, content);
        try { action(path); }
        finally { try { Directory.Delete(root, true); } catch { } }
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

using MediaBrowser.Controller.Entities;
using StrmAssistant.Compatibility;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace StrmAssistant.ContractTests;

internal static class DeferredCleanupQueueContractTests
{
    [ModuleInitializer]
    internal static void RunModuleTests()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("Deferred cleanup queue matches same InternalId and path exactly once", MatchesSameItemOnce),
            ("Deferred cleanup queue does not consume mismatched path", RejectsPathMismatchWithoutConsuming),
            ("Deferred cleanup queue CancelPending removes entry", CancelRemovesEntry),
            ("Deferred cleanup queue preserves explicitly supplied remote path", PreservesExplicitRemotePath)
        };
        var failures = new List<string>();
        Console.WriteLine($"StrmAssistant deferred cleanup queue contract tests: {tests.Length} cases");
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
            throw new InvalidOperationException("Deferred cleanup queue failures: " + string.Join(" | ", failures));
    }

    private static void MatchesSameItemOnce()
    {
        var item = CreateItem(900001, "C:/library/a/movie.strm");
        NativeRemoteDeleteDeferredCleanupQueue.CancelPending(item.InternalId);
        NativeRemoteDeleteDeferredCleanupQueue.MarkPending(item, "/115/movie.mkv");
        AssertTrue(NativeRemoteDeleteDeferredCleanupQueue.TryTake(item, out var pending),
            "Matching ItemRemoved identity did not consume pending cleanup.");
        AssertEqual(item.InternalId, pending.ItemId, "Pending item id mismatch.");
        AssertEqual("C:/library/a/movie.strm", pending.ItemPath.Replace('\\', '/'), "Pending item path mismatch.");
        AssertFalse(NativeRemoteDeleteDeferredCleanupQueue.TryTake(item, out _),
            "Pending cleanup was consumed more than once.");
    }

    private static void RejectsPathMismatchWithoutConsuming()
    {
        var original = CreateItem(900002, "C:/library/a/movie.strm");
        var wrong = CreateItem(900002, "C:/library/b/movie.strm");
        NativeRemoteDeleteDeferredCleanupQueue.CancelPending(original.InternalId);
        NativeRemoteDeleteDeferredCleanupQueue.MarkPending(original, "/115/movie.mkv");
        AssertFalse(NativeRemoteDeleteDeferredCleanupQueue.TryTake(wrong, out _),
            "Same InternalId with different path consumed pending cleanup.");
        AssertTrue(NativeRemoteDeleteDeferredCleanupQueue.TryTake(original, out _),
            "Path-mismatch attempt incorrectly removed the original pending entry.");
    }

    private static void CancelRemovesEntry()
    {
        var item = CreateItem(900003, "C:/library/a/cancel.strm");
        NativeRemoteDeleteDeferredCleanupQueue.MarkPending(item, "/115/cancel.mkv");
        NativeRemoteDeleteDeferredCleanupQueue.CancelPending(item.InternalId);
        AssertFalse(NativeRemoteDeleteDeferredCleanupQueue.TryTake(item, out _),
            "CancelPending left an entry consumable.");
    }

    private static void PreservesExplicitRemotePath()
    {
        var item = CreateItem(900004, "C:/library/a/explicit.strm");
        NativeRemoteDeleteDeferredCleanupQueue.CancelPending(item.InternalId);
        NativeRemoteDeleteDeferredCleanupQueue.MarkPending(item, "/115/explicit.mkv");
        AssertTrue(NativeRemoteDeleteDeferredCleanupQueue.TryTake(item, out var pending),
            "Pending explicit-path cleanup was not consumable.");
        AssertEqual("/115/explicit.mkv", pending.RemotePath, "Explicit remote path was not retained.");
    }

    private static BaseItem CreateItem(long id, string path)
    {
        var assembly = typeof(BaseItem).Assembly;
        BaseItem item = null;
        foreach (var typeName in new[]
        {
            "MediaBrowser.Controller.Entities.Movies.Movie",
            "MediaBrowser.Controller.Entities.TV.Episode"
        })
        {
            var type = assembly.GetType(typeName, false);
            if (type == null || type.IsAbstract) continue;
            item = (BaseItem)Activator.CreateInstance(type, nonPublic: true);
            break;
        }
        if (item == null) throw new InvalidOperationException("No concrete BaseItem type available.");
        SetMember(item, "InternalId", id);
        SetMember(item, "Path", path);
        SetMember(item, "Name", "contract");
        return item;
    }

    private static void SetMember(object target, string name, object value)
    {
        var type = target.GetType();
        for (var cursor = type; cursor != null; cursor = cursor.BaseType)
        {
            var property = cursor.GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var setter = property?.GetSetMethod(true);
            if (setter != null)
            {
                setter.Invoke(target, new[] { value });
                return;
            }
            foreach (var fieldName in new[] { name, "_" + char.ToLowerInvariant(name[0]) + name.Substring(1), "<" + name + ">k__BackingField" })
            {
                var field = cursor.GetField(fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field == null) continue;
                field.SetValue(target, value);
                return;
            }
        }
        throw new InvalidOperationException("Could not set member " + name + " on " + type.FullName);
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

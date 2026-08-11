using StrmAssistant.Compatibility;
using StrmAssistant.Experience;
using System.Runtime.CompilerServices;

namespace StrmAssistant.ContractTests;

internal static class RemoteJournalContractTests
{
    [ModuleInitializer]
    internal static void RunModuleTests()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("Verified-delete journal persists exact transaction identity", JournalRecordsExactIdentity),
            ("Verified-delete journal does not match different provider", JournalDoesNotMatchDifferentProvider),
            ("Verified-delete journal does not match different remote path", JournalDoesNotMatchDifferentPath),
            ("Verified-delete journal does not match different source identity", JournalDoesNotMatchDifferentSource),
            ("Verified-delete journal remove clears retry proof", JournalRemoveClearsProof),
            ("Verified-delete journal file does not expose plain remote identity", JournalFileDoesNotExposePlainIdentity)
        };

        var failures = new List<string>();
        Console.WriteLine($"StrmAssistant remote journal contract tests: {tests.Length} cases");
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
            throw new InvalidOperationException("Remote journal contract failures: " + string.Join(" | ", failures));
    }

    private static void JournalRecordsExactIdentity()
    {
        var plan = UniquePlan("OpenList", "/115/movie.mkv", "https://cdn.example.com/d/115/movie.mkv");
        try
        {
            RemoteDeepDeleteTransactionJournalStore.Record(plan);
            AssertTrue(RemoteDeepDeleteTransactionJournalStore.Contains(plan), "Recorded transaction was not found.");
        }
        finally { RemoteDeepDeleteTransactionJournalStore.Remove(plan); }
    }

    private static void JournalDoesNotMatchDifferentProvider()
    {
        var plan = UniquePlan("OpenList", "/115/movie.mkv", "https://cdn.example.com/d/115/movie.mkv");
        try
        {
            RemoteDeepDeleteTransactionJournalStore.Record(plan);
            var other = Clone(plan);
            other.Provider = "WebDav";
            AssertFalse(RemoteDeepDeleteTransactionJournalStore.Contains(other),
                "Journal proof crossed provider boundary.");
        }
        finally { RemoteDeepDeleteTransactionJournalStore.Remove(plan); }
    }

    private static void JournalDoesNotMatchDifferentPath()
    {
        var plan = UniquePlan("OpenList", "/115/movie.mkv", "https://cdn.example.com/d/115/movie.mkv");
        try
        {
            RemoteDeepDeleteTransactionJournalStore.Record(plan);
            var other = Clone(plan);
            other.RemotePath += ".other";
            AssertFalse(RemoteDeepDeleteTransactionJournalStore.Contains(other),
                "Journal proof matched a different remote path.");
        }
        finally { RemoteDeepDeleteTransactionJournalStore.Remove(plan); }
    }

    private static void JournalDoesNotMatchDifferentSource()
    {
        var plan = UniquePlan("OpenList", "/115/movie.mkv", "https://cdn.example.com/d/115/movie.mkv");
        try
        {
            RemoteDeepDeleteTransactionJournalStore.Record(plan);
            var other = Clone(plan);
            other.SourceTarget = plan.SourceTarget.Replace("cdn.example.com", "other.example.com", StringComparison.Ordinal);
            AssertFalse(RemoteDeepDeleteTransactionJournalStore.Contains(other),
                "Journal proof matched a different source identity.");
        }
        finally { RemoteDeepDeleteTransactionJournalStore.Remove(plan); }
    }

    private static void JournalRemoveClearsProof()
    {
        var plan = UniquePlan("OpenList", "/115/movie.mkv", "https://cdn.example.com/d/115/movie.mkv");
        RemoteDeepDeleteTransactionJournalStore.Record(plan);
        AssertTrue(RemoteDeepDeleteTransactionJournalStore.Contains(plan), "Precondition: journal record missing.");
        RemoteDeepDeleteTransactionJournalStore.Remove(plan);
        AssertFalse(RemoteDeepDeleteTransactionJournalStore.Contains(plan), "Removed journal record still matches.");
    }

    private static void JournalFileDoesNotExposePlainIdentity()
    {
        var marker = Guid.NewGuid().ToString("N");
        var plan = new RemoteDeepDeletePlan
        {
            Provider = "OpenList",
            RemotePath = "/115/secret-" + marker + ".mkv",
            SourceTarget = "https://cdn.example.com/d/115/secret-" + marker + ".mkv"
        };
        try
        {
            RemoteDeepDeleteTransactionJournalStore.Record(plan);
            var path = RemoteDeepDeleteTransactionJournalStore.Path;
            AssertTrue(File.Exists(path), "Journal file was not persisted.");
            var text = File.ReadAllText(path);
            AssertFalse(text.Contains(marker, StringComparison.Ordinal),
                "Journal persisted the raw remote identity in plaintext.");
            AssertFalse(text.Contains("cdn.example.com", StringComparison.OrdinalIgnoreCase),
                "Journal exposed source host in plaintext.");
        }
        finally { RemoteDeepDeleteTransactionJournalStore.Remove(plan); }
    }

    private static RemoteDeepDeletePlan UniquePlan(string provider, string remotePath, string source)
    {
        var marker = Guid.NewGuid().ToString("N");
        var extensionIndex = remotePath.LastIndexOf('.');
        var uniquePath = extensionIndex > 0
            ? remotePath.Insert(extensionIndex, "-" + marker)
            : remotePath + "-" + marker;
        var uniqueSource = source.Replace("movie.mkv", "movie-" + marker + ".mkv", StringComparison.Ordinal);
        return new RemoteDeepDeletePlan
        {
            Provider = provider,
            RemotePath = uniquePath,
            SourceTarget = uniqueSource
        };
    }

    private static RemoteDeepDeletePlan Clone(RemoteDeepDeletePlan plan)
    {
        return new RemoteDeepDeletePlan
        {
            Provider = plan.Provider,
            RemotePath = plan.RemotePath,
            SourceTarget = plan.SourceTarget
        };
    }

    private static void AssertTrue(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void AssertFalse(bool value, string message) => AssertTrue(!value, message);
}

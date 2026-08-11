using MediaBrowser.Model.Serialization;
using StrmAssistant.Metadata;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace StrmAssistant.ContractTests;

internal static class LocalMetadataContractTests
{
    [ModuleInitializer]
    internal static void RunModuleTests()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("Local TMDB source reads an actual JSON document under RootPath", ReadsActualJsonDocument),
            ("Local TMDB source rejects relative RootPath", RejectsRelativeRoot),
            ("Local TMDB source rejects identity path traversal", RejectsPathTraversal),
            ("Local TMDB source disabled performs no read", DisabledSourceDoesNotRead),
            ("Local TMDB source returns false for missing file without inventing metadata", MissingFileReturnsFalse),
            ("Local TMDB identity path sanitizes provider id", IdentityPathSanitizesProviderId),
            ("Local TMDB nested episode identity is deterministic", NestedEpisodeIdentityIsDeterministic)
        };

        var failures = new List<string>();
        Console.WriteLine($"StrmAssistant local metadata contract tests: {tests.Length} cases");
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
            throw new InvalidOperationException("Local metadata contract failures: " + string.Join(" | ", failures));
    }

    private static void ReadsActualJsonDocument()
    {
        WithTempRoot(root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "movie"));
            var file = Path.Combine(root, "movie", "1396.json");
            File.WriteAllText(file,
                "{\"Name\":\"测试电影\",\"OriginalTitle\":\"Original\",\"Overview\":\"overview\",\"ProductionYear\":2026,\"Genres\":[\"Drama\",\"Crime\"],\"ProviderIds\":{\"Tmdb\":\"1396\"}}");
            LocalTmdbMetadataRuntimeSettings.Save(new LocalTmdbMetadataOptions
            {
                Enabled = true,
                RootPath = root
            });
            var store = new LocalTmdbMetadataStore(CreateSerializer());
            var identity = new LocalTmdbMetadataIdentity { Kind = "movie", TmdbId = "1396", RelativePath = Path.Combine("movie", "1396.json") };

            var ok = store.TryRead(identity, out var document, out var fullPath, out var error);
            AssertTrue(ok, "Actual local metadata JSON could not be read: " + error);
            AssertEqual(Path.GetFullPath(file), Path.GetFullPath(fullPath), "Resolved metadata file mismatch.");
            AssertEqual("测试电影", document.Name, "Name was not deserialized.");
            AssertEqual(2026, document.ProductionYear, "ProductionYear mismatch.");
            AssertSequence(document.Genres, "Drama", "Crime");
            AssertEqual("1396", document.ProviderIds["Tmdb"], "ProviderIds were not deserialized.");
        });
    }

    private static void RejectsRelativeRoot()
    {
        LocalTmdbMetadataRuntimeSettings.Save(new LocalTmdbMetadataOptions
        {
            Enabled = true,
            RootPath = "relative-root"
        });
        var store = new LocalTmdbMetadataStore(CreateSerializer());
        var ok = store.TryRead(new LocalTmdbMetadataIdentity { RelativePath = Path.Combine("movie", "1.json") },
            out _, out _, out var error);
        AssertFalse(ok, "Relative RootPath was accepted.");
        AssertContains(error, "rooted", "Relative RootPath error is not explicit.");
    }

    private static void RejectsPathTraversal()
    {
        WithTempRoot(root =>
        {
            LocalTmdbMetadataRuntimeSettings.Save(new LocalTmdbMetadataOptions { Enabled = true, RootPath = root });
            var store = new LocalTmdbMetadataStore(CreateSerializer());
            var ok = store.TryRead(new LocalTmdbMetadataIdentity { RelativePath = Path.Combine("..", "outside.json") },
                out _, out _, out var error);
            AssertFalse(ok, "Identity path traversal escaped RootPath.");
            AssertContains(error, "escaped RootPath", "Traversal rejection reason mismatch.");
        });
    }

    private static void DisabledSourceDoesNotRead()
    {
        WithTempRoot(root =>
        {
            LocalTmdbMetadataRuntimeSettings.Save(new LocalTmdbMetadataOptions { Enabled = false, RootPath = root });
            var proxy = CreateSerializer(out var counter);
            var store = new LocalTmdbMetadataStore(proxy);
            var ok = store.TryRead(new LocalTmdbMetadataIdentity { RelativePath = Path.Combine("movie", "1.json") },
                out _, out _, out var error);
            AssertFalse(ok, "Disabled local source reported success.");
            AssertEqual(0, counter.Value, "Disabled local source still invoked JSON deserialization.");
            AssertContains(error, "disabled", "Disabled source error missing.");
        });
    }

    private static void MissingFileReturnsFalse()
    {
        WithTempRoot(root =>
        {
            LocalTmdbMetadataRuntimeSettings.Save(new LocalTmdbMetadataOptions { Enabled = true, RootPath = root });
            var store = new LocalTmdbMetadataStore(CreateSerializer());
            var ok = store.TryRead(new LocalTmdbMetadataIdentity { RelativePath = Path.Combine("movie", "404.json") },
                out var document, out var fullPath, out var error);
            AssertFalse(ok, "Missing local metadata file reported success.");
            AssertTrue(document == null, "Missing file invented a metadata document.");
            AssertTrue(!string.IsNullOrWhiteSpace(fullPath), "Missing file should still expose the resolved lookup path.");
            AssertTrue(error == null, "Missing file should be a normal miss, not an exception: " + error);
        });
    }

    private static void IdentityPathSanitizesProviderId()
    {
        var path = InvokePrivate<string>("BuildIdPath", "movie", " 12/3:4-5_6 ");
        AssertEqual(Path.Combine("movie", "1234-5_6.json"), path, "Provider id sanitization mismatch.");
    }

    private static void NestedEpisodeIdentityIsDeterministic()
    {
        var path = InvokePrivate<string>("BuildNestedEpisodePath", "1396", (int?)2, (int?)7);
        AssertEqual(Path.Combine("tv", "1396", "season-2", "episode-7.json"), path,
            "Nested episode identity path mismatch.");
    }

    private static IJsonSerializer CreateSerializer()
    {
        return CreateSerializer(out _);
    }

    private static IJsonSerializer CreateSerializer(out Counter counter)
    {
        var proxy = DispatchProxy.Create<IJsonSerializer, JsonSerializerProxy>();
        var implementation = (JsonSerializerProxy)(object)proxy;
        counter = implementation.Counter;
        return proxy;
    }

    private sealed class Counter
    {
        public int Value;
    }

    private sealed class JsonSerializerProxy : DispatchProxy
    {
        public Counter Counter { get; } = new Counter();

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod == null) throw new InvalidOperationException("Missing serializer method.");
            if (targetMethod.Name == "DeserializeFromFile" && targetMethod.IsGenericMethod)
            {
                Counter.Value++;
                var path = Convert.ToString(args[0]);
                var type = targetMethod.GetGenericArguments()[0];
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize(json, type, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            throw new NotSupportedException("Contract serializer does not implement " + targetMethod.Name);
        }
    }

    private static T InvokePrivate<T>(string name, params object[] args)
    {
        var method = typeof(LocalTmdbMetadataStore).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        if (method == null) throw new InvalidOperationException("Missing production method: " + name);
        return (T)method.Invoke(null, args);
    }

    private static void WithTempRoot(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "strmassistant-local-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void AssertSequence(IReadOnlyList<string> actual, params string[] expected)
    {
        if (actual == null || actual.Count != expected.Length || !actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidOperationException("Sequence mismatch. Expected=" + string.Join(",", expected) +
                                                " Actual=" + string.Join(",", actual ?? Array.Empty<string>()));
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

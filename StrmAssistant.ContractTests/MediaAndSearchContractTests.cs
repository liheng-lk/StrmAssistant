using MediaBrowser.Controller.Entities;
using StrmAssistant.MediaEnhance;
using StrmAssistant.Options;
using StrmAssistant.Search;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace StrmAssistant.ContractTests;

internal static class MediaAndSearchContractTests
{
    [ModuleInitializer]
    internal static void RunModuleTests()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("Extraction blacklist disabled never skips", ExtractionBlacklistDisabledNeverSkips),
            ("Extraction blacklist matches keyword in name case-insensitively", ExtractionBlacklistMatchesNameKeyword),
            ("Extraction blacklist matches keyword in path", ExtractionBlacklistMatchesPathKeyword),
            ("Extraction blacklist matches tags case-insensitively", ExtractionBlacklistMatchesTag),
            ("MediaInfo sync mapping creates stable logical key", MediaInfoSyncCreatesStableLogicalKey),
            ("MediaInfo sync mapping enforces local-root path boundary", MediaInfoSyncMappingBoundary),
            ("MediaInfo sync rejects unsafe logical mapping and stays under shared root", MediaInfoSyncRejectsUnsafeLogicalRoot),
            ("Advanced Chinese FTS tokenizer detection recognizes simple", FtsDetectsSimpleTokenizer),
            ("Advanced Chinese FTS tokenizer detection recognizes unicode61", FtsDetectsUnicodeTokenizer),
            ("Advanced Chinese FTS migration script is transactional and targets one FTS table", FtsMigrationScriptIsTransactional),
            ("Advanced Chinese FTS migration script uses pre-4.9 Album column", FtsMigrationScriptUsesLegacyAlbumColumn)
        };

        var failed = new List<string>();
        Console.WriteLine($"StrmAssistant media/search contract tests: {tests.Length} cases");
        foreach (var test in tests)
        {
            try
            {
                test.Body();
                Console.WriteLine($"[PASS] {test.Name}");
            }
            catch (Exception ex)
            {
                failed.Add(test.Name + ": " + ex.GetBaseException().Message);
                Console.Error.WriteLine($"[FAIL] {test.Name}");
                Console.Error.WriteLine(ex.ToString());
            }
        }

        if (failed.Count > 0)
            throw new InvalidOperationException("Media/search contract failures: " + string.Join(" | ", failed));
    }

    private static void ExtractionBlacklistDisabledNeverSkips()
    {
        var item = CreateItem("Blocked Movie", Path.Combine(Path.GetTempPath(), "blocked", "movie.mkv"), "skip-tag");
        var options = new MediaInfoExtractOptions
        {
            EnableExtractionBlacklist = false,
            ExtractionBlacklistTags = "skip-tag",
            ExtractionBlacklistKeywords = "blocked"
        };
        var skipped = MediaExtractionFilter.ShouldSkip(item, options, out var reason);
        AssertFalse(skipped, "Disabled blacklist skipped an item.");
        AssertTrue(reason == null, "Disabled blacklist returned a skip reason.");
    }

    private static void ExtractionBlacklistMatchesNameKeyword()
    {
        var item = CreateItem("Concert REMUX", Path.Combine(Path.GetTempPath(), "media", "concert.mkv"));
        var options = new MediaInfoExtractOptions
        {
            EnableExtractionBlacklist = true,
            ExtractionBlacklistKeywords = "remux"
        };
        AssertTrue(MediaExtractionFilter.ShouldSkip(item, options, out var reason), "Name keyword did not skip item.");
        AssertEqual("keyword:remux", reason, "Name keyword reason mismatch.");
    }

    private static void ExtractionBlacklistMatchesPathKeyword()
    {
        var item = CreateItem("Movie", Path.Combine(Path.GetTempPath(), "Samples", "movie.mkv"));
        var options = new MediaInfoExtractOptions
        {
            EnableExtractionBlacklist = true,
            ExtractionBlacklistKeywords = "samples"
        };
        AssertTrue(MediaExtractionFilter.ShouldSkip(item, options, out var reason), "Path keyword did not skip item.");
        AssertEqual("keyword:samples", reason, "Path keyword reason mismatch.");
    }

    private static void ExtractionBlacklistMatchesTag()
    {
        var item = CreateItem("Movie", Path.Combine(Path.GetTempPath(), "media", "movie.mkv"), "NoProbe");
        var options = new MediaInfoExtractOptions
        {
            EnableExtractionBlacklist = true,
            ExtractionBlacklistTags = "noprobe"
        };
        AssertTrue(MediaExtractionFilter.ShouldSkip(item, options, out var reason), "Tag did not skip item.");
        AssertEqual("tag:NoProbe", reason, "Tag reason should preserve actual matched tag text.");
    }

    private static void MediaInfoSyncCreatesStableLogicalKey()
    {
        WithTempRoot(root =>
        {
            var localRoot = Directory.CreateDirectory(Path.Combine(root, "local-media")).FullName;
            var season = Directory.CreateDirectory(Path.Combine(localRoot, "Series", "Season 01")).FullName;
            var shared = Directory.CreateDirectory(Path.Combine(root, "shared")).FullName;
            var item = CreateItem("Episode", Path.Combine(season, "S01E01.mkv"));
            var result = MediaInfoSyncPathResolver.Resolve(item, shared, localRoot + " => tv");

            AssertTrue(result.Success, "Sync resolution failed: " + result.Error);
            AssertTrue(result.MappingMatched, "Expected mapping to match.");
            var expectedKey = "tv/Series/Season 01/S01E01-mediainfo.json";
            AssertEqual(expectedKey.Replace('\\', '/'), result.SyncKey.Replace('\\', '/'), "Stable sync key mismatch.");
            AssertPathUnder(shared, result.JsonPath, "Resolved JSON path escaped shared root.");
        });
    }

    private static void MediaInfoSyncMappingBoundary()
    {
        WithTempRoot(root =>
        {
            var media = Directory.CreateDirectory(Path.Combine(root, "media")).FullName;
            var colliding = Directory.CreateDirectory(Path.Combine(root, "media-extra")).FullName;
            var folder = Directory.CreateDirectory(Path.Combine(media, "Movie")).FullName;
            var shared = Directory.CreateDirectory(Path.Combine(root, "shared")).FullName;
            var item = CreateItem("Movie", Path.Combine(folder, "movie.mkv"));
            var mappings = colliding + " => wrong\n" + media + " => correct";
            var result = MediaInfoSyncPathResolver.Resolve(item, shared, mappings);

            AssertTrue(result.Success, "Sync resolution failed: " + result.Error);
            AssertTrue(result.SyncKey.Replace('\\', '/').StartsWith("correct/", StringComparison.Ordinal),
                "Textual local-root collision selected the wrong mapping: " + result.SyncKey);
        });
    }

    private static void MediaInfoSyncRejectsUnsafeLogicalRoot()
    {
        WithTempRoot(root =>
        {
            var media = Directory.CreateDirectory(Path.Combine(root, "media")).FullName;
            var folder = Directory.CreateDirectory(Path.Combine(media, "Movie")).FullName;
            var shared = Directory.CreateDirectory(Path.Combine(root, "shared")).FullName;
            var item = CreateItem("Movie", Path.Combine(folder, "movie.mkv"));
            var result = MediaInfoSyncPathResolver.Resolve(item, shared, media + " => ../escape");

            AssertTrue(result.Success, "Unsafe mapping should be ignored and safe fallback should resolve: " + result.Error);
            AssertFalse(result.MappingMatched, "Unsafe logical root was accepted as a mapping.");
            AssertPathUnder(shared, result.JsonPath, "Fallback JSON path escaped shared root.");
        });
    }

    private static void FtsDetectsSimpleTokenizer()
    {
        var detected = InvokePrivateStatic<string>(typeof(AdvancedChineseSearchMigration), "DetectTokenizer",
            "CREATE VIRTUAL TABLE fts_search9 USING FTS5(Name, tokenize=\"simple\", prefix='1 2 3 4')");
        AssertEqual("simple", detected, "Simple tokenizer detection failed.");
    }

    private static void FtsDetectsUnicodeTokenizer()
    {
        var detected = InvokePrivateStatic<string>(typeof(AdvancedChineseSearchMigration), "DetectTokenizer",
            "CREATE VIRTUAL TABLE fts_search9 USING FTS5(Name, tokenize=\"unicode61 remove_diacritics 2\", prefix='1 2 3 4')");
        AssertEqual("unicode61 remove_diacritics 2", detected, "unicode61 tokenizer detection failed.");
    }

    private static void FtsMigrationScriptIsTransactional()
    {
        var script = InvokePrivateStatic<string>(typeof(AdvancedChineseSearchMigration), "BuildMigrationScript",
            "fts_search9", "simple", new Version(4, 10, 0, 0), "C:\\tokenizer\\simple.dll");
        AssertContains(script, ".bail on", "Migration must enable sqlite bail mode.");
        AssertContains(script, "BEGIN IMMEDIATE;", "Migration must start a transaction.");
        AssertContains(script, "DROP TABLE IF EXISTS fts_search9;", "Expected only target FTS table rebuild.");
        AssertContains(script, "CREATE VIRTUAL TABLE fts_search9", "Expected target FTS table creation.");
        AssertContains(script, "COMMIT;", "Migration must commit explicitly.");
        AssertContains(script, "simple_query('中文搜索')", "Simple tokenizer smoke query missing.");
        AssertContains(script, "AlbumId", "4.9+ migration should resolve album name through AlbumId.");
        AssertFalse(script.Contains("DROP TABLE IF EXISTS MediaItems", StringComparison.OrdinalIgnoreCase),
            "Migration must never drop MediaItems.");
        AssertFalse(script.Contains("DELETE FROM MediaItems", StringComparison.OrdinalIgnoreCase),
            "Migration must never delete MediaItems rows.");
    }

    private static void FtsMigrationScriptUsesLegacyAlbumColumn()
    {
        var script = InvokePrivateStatic<string>(typeof(AdvancedChineseSearchMigration), "BuildMigrationScript",
            "fts_search8", "unicode61 remove_diacritics 2", new Version(4, 8, 2, 0), null);
        AssertContains(script, "DROP TABLE IF EXISTS fts_search8;", "Legacy migration should target fts_search8.");
        AssertContains(script, "replace(replace(Album,'''',''),'.','')", "Pre-4.9 schema should use Album column.");
        AssertFalse(script.Contains("simple_query", StringComparison.OrdinalIgnoreCase),
            "Restore/unicode migration must not call simple_query.");
    }

    private static BaseItem CreateItem(string name, string path, params string[] tags)
    {
        var assembly = typeof(BaseItem).Assembly;
        var candidates = new[]
        {
            "MediaBrowser.Controller.Entities.Movies.Movie",
            "MediaBrowser.Controller.Entities.TV.Episode",
            "MediaBrowser.Controller.Entities.Folder"
        };
        Type type = null;
        foreach (var nameCandidate in candidates)
        {
            var candidate = assembly.GetType(nameCandidate, false);
            if (candidate != null && !candidate.IsAbstract && typeof(BaseItem).IsAssignableFrom(candidate))
            {
                type = candidate;
                break;
            }
        }
        if (type == null) throw new InvalidOperationException("No concrete BaseItem test type was found in the Emby Core assembly.");
        var item = (BaseItem)Activator.CreateInstance(type, nonPublic: true);
        SetProperty(item, "Name", name);
        SetProperty(item, "Path", path);
        if (tags != null && tags.Length > 0) SetStringCollectionProperty(item, "Tags", tags);
        return item;
    }

    private static void SetProperty(object target, string propertyName, object value)
    {
        var property = target.GetType().GetProperty(propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        if (property == null) throw new InvalidOperationException("Missing property " + propertyName + " on " + target.GetType().FullName);
        var setter = property.GetSetMethod(true);
        if (setter == null) throw new InvalidOperationException("Property " + propertyName + " has no setter on " + target.GetType().FullName);
        setter.Invoke(target, new[] { value });
    }

    private static void SetStringCollectionProperty(object target, string propertyName, string[] values)
    {
        var property = target.GetType().GetProperty(propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        if (property == null) throw new InvalidOperationException("Missing property " + propertyName);
        object value;
        if (property.PropertyType == typeof(string[])) value = values;
        else if (property.PropertyType.IsAssignableFrom(typeof(List<string>))) value = values.ToList();
        else if (typeof(IEnumerable<string>).IsAssignableFrom(property.PropertyType)) value = values.ToList();
        else throw new InvalidOperationException("Unsupported string collection property type: " + property.PropertyType.FullName);
        property.GetSetMethod(true)?.Invoke(target, new[] { value });
    }

    private static T InvokePrivateStatic<T>(Type type, string name, params object[] args)
    {
        var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        if (method == null) throw new InvalidOperationException("Private static method not found: " + type.FullName + "." + name);
        return (T)method.Invoke(null, args);
    }

    private static void WithTempRoot(Action<string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "strmassistant-contract2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { body(root); }
        finally { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { } }
    }

    private static void AssertPathUnder(string root, string candidate, string message)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullCandidate.StartsWith(fullRoot, comparison))
            throw new InvalidOperationException(message + " Root=" + fullRoot + ", Candidate=" + fullCandidate);
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

    private static void AssertContains(string text, string expected, string message)
    {
        if (text?.Contains(expected, StringComparison.Ordinal) != true)
            throw new InvalidOperationException(message + " Missing=" + expected);
    }
}

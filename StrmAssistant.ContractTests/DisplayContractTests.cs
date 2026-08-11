using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using StrmAssistant.Compatibility;
using StrmAssistant.Experience;
using System.Runtime.CompilerServices;

namespace StrmAssistant.ContractTests;

internal static class DisplayContractTests
{
    [ModuleInitializer]
    internal static void RunModuleTests()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("Multi-version enhancement sorts highest quality first", MultiVersionSortsHighestQualityFirst),
            ("Multi-version enhancement builds informative source names", MultiVersionBuildsNames),
            ("Multi-version enhancement disambiguates duplicate names", MultiVersionDisambiguatesDuplicates),
            ("Multi-version disabled preserves original order and names", MultiVersionDisabledPreservesOriginal),
            ("Multi-version separator is bounded", MultiVersionSeparatorIsBounded),
            ("Natural title sort orders 2 before 10", NaturalSortOrdersNumericTitles),
            ("Natural title sort normalizes leading zeros", NaturalSortNormalizesLeadingZeros),
            ("Natural title sort disabled leaves value untouched", NaturalSortDisabledLeavesValueUntouched)
        };

        var failed = new List<string>();
        Console.WriteLine($"StrmAssistant display contract tests: {tests.Length} cases");
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
            throw new InvalidOperationException("Display contract failures: " + string.Join(" | ", failed));
    }

    private static void MultiVersionSortsHighestQualityFirst()
    {
        MultiVersionRuntimeSettings.Save(new MultiVersionRuntimeOptions
        {
            Enabled = true,
            RenameSources = false,
            SortHighestQualityFirst = true
        });

        var source720 = Source("720", "C:\\media\\movie-720.mkv", 1280, 720, 4_000_000, "mkv");
        var source2160 = Source("2160", "C:\\media\\movie-2160.mkv", 3840, 2160, 12_000_000, "mkv");
        var enhanced = MultiVersionRuntimeSettings.Enhance(new[] { source720, source2160 });

        AssertSame(source2160, enhanced[0], "2160p source should sort before 720p source.");
        AssertSame(source720, enhanced[1], "720p source order mismatch.");
    }

    private static void MultiVersionBuildsNames()
    {
        MultiVersionRuntimeSettings.Save(new MultiVersionRuntimeOptions
        {
            Enabled = true,
            RenameSources = true,
            SortHighestQualityFirst = false,
            IncludeFileName = true,
            IncludeContainer = true,
            Separator = " | "
        });

        var source = Source("old", "C:\\media\\Movie.Remux.mkv", 3840, 2160, 10_000_000, "mkv");
        var enhanced = MultiVersionRuntimeSettings.Enhance(new[] { source, Source("other", "C:\\media\\other.mkv", 1280, 720, 2_000_000, "mkv") });
        var name = enhanced[0].Name;
        AssertContains(name, "2160p", "Quality label missing from source name.");
        AssertContains(name, "MKV", "Container missing from source name.");
        AssertContains(name, "Movie.Remux", "Filename missing from source name.");
        AssertContains(name, " | ", "Configured separator missing from source name.");
    }

    private static void MultiVersionDisambiguatesDuplicates()
    {
        MultiVersionRuntimeSettings.Save(new MultiVersionRuntimeOptions
        {
            Enabled = true,
            RenameSources = true,
            IncludeFileName = true,
            IncludeContainer = false,
            SortHighestQualityFirst = false,
            Separator = " · "
        });

        var first = Source("a", "C:\\one\\movie.mkv", 1920, 1080, 5_000_000, "mkv");
        var second = Source("b", "D:\\two\\movie.mkv", 1920, 1080, 5_000_000, "mkv");
        var enhanced = MultiVersionRuntimeSettings.Enhance(new[] { first, second });

        AssertFalse(string.Equals(enhanced[0].Name, enhanced[1].Name, StringComparison.OrdinalIgnoreCase),
            "Duplicate generated source names were not disambiguated.");
        AssertTrue(enhanced[1].Name.EndsWith(" #2", StringComparison.Ordinal),
            "Second duplicate source should receive #2 suffix. Actual=" + enhanced[1].Name);
    }

    private static void MultiVersionDisabledPreservesOriginal()
    {
        MultiVersionRuntimeSettings.Save(new MultiVersionRuntimeOptions
        {
            Enabled = false,
            RenameSources = true,
            SortHighestQualityFirst = true
        });
        var first = Source("first", "C:\\media\\low.mkv", 1280, 720, 1_000_000, "mkv");
        var second = Source("second", "C:\\media\\high.mkv", 3840, 2160, 10_000_000, "mkv");
        var enhanced = MultiVersionRuntimeSettings.Enhance(new[] { first, second });

        AssertSame(first, enhanced[0], "Disabled enhancement changed source order.");
        AssertSame(second, enhanced[1], "Disabled enhancement changed source order.");
        AssertEqual("first", enhanced[0].Name, "Disabled enhancement renamed source.");
        AssertEqual("second", enhanced[1].Name, "Disabled enhancement renamed source.");
    }

    private static void MultiVersionSeparatorIsBounded()
    {
        var saved = MultiVersionRuntimeSettings.Save(new MultiVersionRuntimeOptions
        {
            Enabled = true,
            Separator = "12345678901234567890"
        });
        AssertTrue(saved.Separator.Length <= 12, "Separator was not bounded to 12 characters.");
    }

    private static void NaturalSortOrdersNumericTitles()
    {
        UiSortRuntimeSettings.Save(new UiSortRuntimeOptions { Enabled = true, NaturalTitleSort = true });
        var two = "Episode 2";
        var ten = "Episode 10";
        NaturalTitleSortPatches.CreateSortNamePostfix(ref two);
        NaturalTitleSortPatches.CreateSortNamePostfix(ref ten);
        AssertTrue(string.CompareOrdinal(two, ten) < 0,
            "Natural sort transformation does not order 2 before 10. two=" + two + ", ten=" + ten);
    }

    private static void NaturalSortNormalizesLeadingZeros()
    {
        UiSortRuntimeSettings.Save(new UiSortRuntimeOptions { Enabled = true, NaturalTitleSort = true });
        var plain = "Episode 2";
        var padded = "Episode 002";
        NaturalTitleSortPatches.CreateSortNamePostfix(ref plain);
        NaturalTitleSortPatches.CreateSortNamePostfix(ref padded);
        AssertEqual(plain, padded, "Leading zeros should normalize to the same numeric sort key.");
    }

    private static void NaturalSortDisabledLeavesValueUntouched()
    {
        UiSortRuntimeSettings.Save(new UiSortRuntimeOptions { Enabled = false, NaturalTitleSort = true });
        var value = "Episode 10";
        NaturalTitleSortPatches.CreateSortNamePostfix(ref value);
        AssertEqual("Episode 10", value, "Disabled natural sorting mutated sort name.");
    }

    private static MediaSourceInfo Source(string name, string path, int width, int height, int bitrate, string container)
    {
        return new MediaSourceInfo
        {
            Name = name,
            Path = path,
            Bitrate = bitrate,
            Container = container,
            MediaStreams = new List<MediaStream>
            {
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Width = width,
                    Height = height
                }
            }
        };
    }

    private static void AssertSame(object expected, object actual, string message)
    {
        if (!ReferenceEquals(expected, actual)) throw new InvalidOperationException(message);
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
            throw new InvalidOperationException(message + " Actual=" + text);
    }
}

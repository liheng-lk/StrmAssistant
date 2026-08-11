using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using StrmAssistant.MediaEnhance;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace StrmAssistant.ContractTests;

internal static class MediaInfoShadowContractTests
{
    [ModuleInitializer]
    internal static void RunModuleTests()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("MediaInfo shadow identity ignores signed URL query and fragment", ShadowIdentityIgnoresQueryAndFragment),
            ("MediaInfo shadow identity normalizes HTTP authority case", ShadowIdentityNormalizesAuthorityCase),
            ("MediaInfo shadow identity changes when remote host changes", ShadowIdentityChangesOnHostChange),
            ("MediaInfo shadow identity changes when remote path changes", ShadowIdentityChangesOnPathChange),
            ("MediaInfo shadow identity decodes equivalent escaped path", ShadowIdentityDecodesEquivalentPath),
            ("Video shadow requires an internal video stream", VideoShadowRequiresInternalVideo),
            ("External video stream cannot satisfy video shadow core", ExternalVideoCannotSatisfyCore),
            ("Audio shadow requires an internal audio stream", AudioShadowRequiresInternalAudio),
            ("Internal audio alone cannot satisfy video shadow core", AudioOnlyCannotSatisfyVideo),
            ("Fixed-time shadow fingerprint comparison rejects changed fingerprint", FixedTimeComparisonRejectsChangedFingerprint)
        };

        var failures = new List<string>();
        Console.WriteLine($"StrmAssistant MediaInfo shadow contract tests: {tests.Length} cases");
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
            throw new InvalidOperationException("MediaInfo shadow contract failures: " + string.Join(" | ", failures));
    }

    private static void ShadowIdentityIgnoresQueryAndFragment()
    {
        var left = Canonical("https://cdn.example.com/d/115/movie.mkv?sign=aaa&expires=1#x");
        var right = Canonical("https://cdn.example.com/d/115/movie.mkv?sign=bbb&expires=999#y");
        AssertEqual(left, right, "Signed URL query/fragment changed shadow identity.");
    }

    private static void ShadowIdentityNormalizesAuthorityCase()
    {
        var left = Canonical("HTTPS://CDN.Example.COM/d/115/movie.mkv?x=1");
        var right = Canonical("https://cdn.example.com/d/115/movie.mkv?x=2");
        AssertEqual(left, right, "HTTP scheme/host case should normalize to one identity.");
    }

    private static void ShadowIdentityChangesOnHostChange()
    {
        var left = Canonical("https://cdn-a.example.com/d/115/movie.mkv?x=1");
        var right = Canonical("https://cdn-b.example.com/d/115/movie.mkv?x=1");
        AssertFalse(string.Equals(left, right, StringComparison.Ordinal),
            "Different remote authorities shared a shadow identity.");
    }

    private static void ShadowIdentityChangesOnPathChange()
    {
        var left = Canonical("https://cdn.example.com/d/115/movie-a.mkv?x=1");
        var right = Canonical("https://cdn.example.com/d/115/movie-b.mkv?x=1");
        AssertFalse(string.Equals(left, right, StringComparison.Ordinal),
            "Different remote media paths shared a shadow identity.");
    }

    private static void ShadowIdentityDecodesEquivalentPath()
    {
        var left = Canonical("https://cdn.example.com/d/115/%E7%94%B5%E5%BD%B1/movie.mkv?x=1");
        var right = Canonical("https://cdn.example.com/d/115/电影/movie.mkv?x=2");
        AssertEqual(left, right, "Equivalent escaped/unescaped URL paths produced different identities.");
    }

    private static void VideoShadowRequiresInternalVideo()
    {
        var video = CreateVideoItem();
        var streams = new List<MediaStream>
        {
            new MediaStream { Type = MediaStreamType.Video, IsExternal = false },
            new MediaStream { Type = MediaStreamType.Audio, IsExternal = false }
        };
        AssertTrue(HasExpectedCoreStream(video, streams), "Internal video stream did not satisfy video shadow core.");
    }

    private static void ExternalVideoCannotSatisfyCore()
    {
        var video = CreateVideoItem();
        var streams = new List<MediaStream>
        {
            new MediaStream { Type = MediaStreamType.Video, IsExternal = true },
            new MediaStream { Type = MediaStreamType.Subtitle, IsExternal = true }
        };
        AssertFalse(HasExpectedCoreStream(video, streams), "External video stream incorrectly satisfied video shadow core.");
    }

    private static void AudioShadowRequiresInternalAudio()
    {
        var audio = CreateAudioItem();
        var streams = new List<MediaStream>
        {
            new MediaStream { Type = MediaStreamType.Audio, IsExternal = false }
        };
        AssertTrue(HasExpectedCoreStream(audio, streams), "Internal audio stream did not satisfy audio shadow core.");
    }

    private static void AudioOnlyCannotSatisfyVideo()
    {
        var video = CreateVideoItem();
        var streams = new List<MediaStream>
        {
            new MediaStream { Type = MediaStreamType.Audio, IsExternal = false }
        };
        AssertFalse(HasExpectedCoreStream(video, streams), "Audio-only stream set incorrectly satisfied video shadow core.");
    }

    private static void FixedTimeComparisonRejectsChangedFingerprint()
    {
        AssertTrue(FixedEquals("abcdef", "abcdef"), "Equal fingerprint was rejected.");
        AssertFalse(FixedEquals("abcdef", "abcdeg"), "Changed fingerprint was accepted.");
        AssertFalse(FixedEquals("abcdef", "abc"), "Different-length fingerprint was accepted.");
    }

    private static string Canonical(string value)
    {
        return InvokePrivate<string>("CanonicalizeShortcutTarget", value);
    }

    private static bool FixedEquals(string left, string right)
    {
        return InvokePrivate<bool>("FixedTimeEquals", left, right);
    }

    private static bool HasExpectedCoreStream(BaseItem item, IEnumerable<MediaStream> streams)
    {
        return InvokePrivate<bool>("HasExpectedCoreStream", item, streams);
    }

    private static T InvokePrivate<T>(string methodName, params object[] args)
    {
        var method = typeof(MediaInfoReliabilityShadowStore).GetMethod(methodName,
            BindingFlags.Static | BindingFlags.NonPublic);
        if (method == null) throw new InvalidOperationException("Missing production shadow method: " + methodName);
        return (T)method.Invoke(null, args);
    }

    private static BaseItem CreateVideoItem()
    {
        return CreateConcreteItem(new[]
        {
            "MediaBrowser.Controller.Entities.Movies.Movie",
            "MediaBrowser.Controller.Entities.TV.Episode",
            "MediaBrowser.Controller.Entities.Video"
        });
    }

    private static BaseItem CreateAudioItem()
    {
        return CreateConcreteItem(new[]
        {
            "MediaBrowser.Controller.Entities.Audio.Audio",
            "MediaBrowser.Controller.Entities.Audio.MusicAudio"
        });
    }

    private static BaseItem CreateConcreteItem(IEnumerable<string> names)
    {
        var assembly = typeof(BaseItem).Assembly;
        foreach (var name in names)
        {
            var type = assembly.GetType(name, false);
            if (type == null || type.IsAbstract || !typeof(BaseItem).IsAssignableFrom(type)) continue;
            try
            {
                return (BaseItem)Activator.CreateInstance(type, nonPublic: true);
            }
            catch { }
        }
        throw new InvalidOperationException("No compatible concrete Emby item type was available for shadow contract testing.");
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

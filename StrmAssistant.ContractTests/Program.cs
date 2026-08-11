using StrmAssistant.Compatibility;
using StrmAssistant.Experience;
using StrmAssistant.IntroSkip;
using StrmAssistant.Options;
using System.Globalization;

namespace StrmAssistant.ContractTests;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("DeepDelete deletes allowed target and same-stem sidecars but preserves STRM source", DeepDeleteDeletesTargetAndSidecarsButPreservesStrm),
            ("DeepDelete refuses target outside allowed root", DeepDeleteBlocksOutsideAllowedRoot),
            ("DeepDelete refuses HTTP remote target in local executor", DeepDeleteRefusesHttpTarget),
            ("DeepDelete dry-run performs no filesystem deletion", DeepDeleteDryRunDoesNotDelete),
            ("DeepDelete empty-directory cleanup never deletes allowed root", DeepDeleteEmptyDirectoryCleanupStopsAtAllowedRoot),
            ("IntroDB.app canonical payload parses credits without inventing intro", ParseIntroDbAppCanonicalPayload),
            ("TheIntroDB canonical payload parses intro and credits", ParseTheIntroDbCanonicalPayload),
            ("Intro providers merge complementary intro and credits", MergeIntroProviders),
            ("Intro confidence 87 percent normalizes to 0.87", IntroConfidenceNormalizesPercent),
            ("Custom IntroDB URL escapes identity values", BuildCustomIntroDbUrlEscapesValues),
            ("Custom IntroDB URL rejects non-http schemes", BuildCustomIntroDbUrlRejectsUnsafeScheme),
            ("Remote path normalization collapses segments safely", RemotePathNormalization),
            ("Remote allowed-root check enforces path-segment boundary", RemoteAllowedRootBoundary),
            ("Remote mapping parser prefers longest source prefix", RemoteMappingLongestPrefixFirst),
            ("Manual remote mapping accepts exact child path", RemotePlanSafetyAcceptsChildPath),
            ("Manual remote mapping rejects textual prefix without path boundary", RemotePlanSafetyRejectsPrefixCollision),
            ("Manual remote mapping rejects authority mismatch", RemotePlanSafetyRejectsAuthorityMismatch),
            ("OpenList structured 200/api200 overrides fuzzy missing", ProbeSafetyStructuredSuccessWins),
            ("OpenList authorization failure can never be treated as missing", ProbeSafetyRejectsAuthorizationFailure),
            ("OpenList backend failure can never be treated as missing", ProbeSafetyRejectsBackendFailure)
        };

        Console.WriteLine($"StrmAssistant contract tests: {tests.Length} cases");
        foreach (var test in tests)
        {
            try
            {
                test.Body();
                _passed++;
                Console.WriteLine($"[PASS] {test.Name}");
            }
            catch (Exception ex)
            {
                _failed++;
                Console.Error.WriteLine($"[FAIL] {test.Name}");
                Console.Error.WriteLine(ex.ToString());
            }
        }

        Console.WriteLine($"Contract test summary: passed={_passed}, failed={_failed}");
        return _failed == 0 ? 0 : 1;
    }

    private static void DeepDeleteDeletesTargetAndSidecarsButPreservesStrm()
    {
        WithTempTree(root =>
        {
            var mediaRoot = Directory.CreateDirectory(Path.Combine(root, "media")).FullName;
            var sourceRoot = Directory.CreateDirectory(Path.Combine(root, "strm")).FullName;
            var target = Path.Combine(mediaRoot, "movie.mkv");
            var subtitle = Path.Combine(mediaRoot, "movie.srt");
            var nfo = Path.Combine(mediaRoot, "movie.nfo");
            var image = Path.Combine(mediaRoot, "movie.jpg");
            var unrelated = Path.Combine(mediaRoot, "other.srt");
            File.WriteAllText(target, "video");
            File.WriteAllText(subtitle, "sub");
            File.WriteAllText(nfo, "nfo");
            File.WriteAllText(image, "jpg");
            File.WriteAllText(unrelated, "keep");
            var strm = Path.Combine(sourceRoot, "movie.strm");
            File.WriteAllText(strm, new Uri(target).AbsoluteUri);

            var options = LocalDeleteOptions(mediaRoot, dryRun: false, associated: true);
            var service = new DeepDeleteService();
            var plan = service.BuildPlan(strm, options);

            Check.True(plan.HasResolvedMediaTarget, "Expected resolved media target.");
            Check.False(plan.HasBlockedEntries, "No planned entry should be blocked.");
            Check.ContainsPath(plan.Entries.Select(e => e.Path), target);
            Check.ContainsPath(plan.Entries.Select(e => e.Path), subtitle);
            Check.ContainsPath(plan.Entries.Select(e => e.Path), nfo);
            Check.ContainsPath(plan.Entries.Select(e => e.Path), image);
            Check.False(plan.Entries.Any(e => SamePath(e.Path, strm)), "STRM source must not be pre-deleted by DeepDeleteService.");
            Check.False(plan.Entries.Any(e => SamePath(e.Path, unrelated)), "Unrelated sidecar must not be included.");

            var result = service.Execute(plan, options);
            Check.Empty(result.Errors, "Filesystem execution returned errors.");
            Check.False(File.Exists(target), "Target file still exists after confirmed local deep delete.");
            Check.False(File.Exists(subtitle), "Same-stem subtitle still exists.");
            Check.False(File.Exists(nfo), "Same-stem NFO still exists.");
            Check.False(File.Exists(image), "Same-stem image still exists.");
            Check.True(File.Exists(unrelated), "Unrelated file was deleted.");
            Check.True(File.Exists(strm), "STRM source was deleted before the Emby item deletion phase.");
        });
    }

    private static void DeepDeleteBlocksOutsideAllowedRoot()
    {
        WithTempTree(root =>
        {
            var allowed = Directory.CreateDirectory(Path.Combine(root, "allowed")).FullName;
            var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            var target = Path.Combine(outside, "movie.mkv");
            File.WriteAllText(target, "video");
            var strm = Path.Combine(source, "movie.strm");
            File.WriteAllText(strm, new Uri(target).AbsoluteUri);

            var options = LocalDeleteOptions(allowed, dryRun: false, associated: false);
            var service = new DeepDeleteService();
            var plan = service.BuildPlan(strm, options);
            Check.True(plan.HasBlockedEntries, "Outside-root target must be blocked in the plan.");
            Check.True(plan.Entries.Any(e => SamePath(e.Path, target) && !e.Allowed), "Target should be explicitly marked blocked.");

            var result = service.Execute(plan, options);
            Check.True(File.Exists(target), "Blocked target was physically deleted.");
            Check.True(result.SkippedPaths.Any(p => SamePath(p, target)), "Blocked target should be reported as skipped.");
        });
    }

    private static void DeepDeleteRefusesHttpTarget()
    {
        WithTempTree(root =>
        {
            var strm = Path.Combine(root, "remote.strm");
            File.WriteAllText(strm, "https://example.invalid/d/media/movie.mkv?token=secret");
            var options = LocalDeleteOptions(root, dryRun: false, associated: true);
            var plan = new DeepDeleteService().BuildPlan(strm, options);

            Check.False(plan.HasResolvedMediaTarget, "HTTP target must not be converted into a local filesystem target.");
            Check.True(plan.Entries.Count == 0, "HTTP target must not create local delete entries.");
            Check.True(plan.Warnings.Any(w => w.Contains("Remote STRM target is not deletable", StringComparison.OrdinalIgnoreCase)),
                "Expected explicit remote-target warning.");
        });
    }

    private static void DeepDeleteDryRunDoesNotDelete()
    {
        WithTempTree(root =>
        {
            var mediaRoot = Directory.CreateDirectory(Path.Combine(root, "media")).FullName;
            var sourceRoot = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            var target = Path.Combine(mediaRoot, "movie.mkv");
            File.WriteAllText(target, "video");
            var strm = Path.Combine(sourceRoot, "movie.strm");
            File.WriteAllText(strm, new Uri(target).AbsoluteUri);

            var options = LocalDeleteOptions(mediaRoot, dryRun: true, associated: false);
            var plan = new DeepDeleteService().BuildPlan(strm, options);
            var result = new DeepDeleteService().Execute(plan, options);

            Check.True(result.DryRun, "Result must report DryRun.");
            Check.True(File.Exists(target), "Dry-run deleted the media target.");
            Check.True(File.Exists(strm), "Dry-run deleted the STRM source.");
            Check.True(result.SkippedPaths.Any(p => SamePath(p, target)), "Dry-run target should be reported as skipped.");
        });
    }

    private static void DeepDeleteEmptyDirectoryCleanupStopsAtAllowedRoot()
    {
        WithTempTree(root =>
        {
            var mediaRoot = Directory.CreateDirectory(Path.Combine(root, "allowed-root")).FullName;
            var nested = Directory.CreateDirectory(Path.Combine(mediaRoot, "season", "episode")).FullName;
            var sourceRoot = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            var target = Path.Combine(nested, "episode.mkv");
            File.WriteAllText(target, "video");
            var strm = Path.Combine(sourceRoot, "episode.strm");
            File.WriteAllText(strm, new Uri(target).AbsoluteUri);

            var options = LocalDeleteOptions(mediaRoot, dryRun: false, associated: false);
            options.DeepDeleteEmptyDirectories = true;
            var service = new DeepDeleteService();
            var result = service.Execute(service.BuildPlan(strm, options), options);

            Check.Empty(result.Errors, "Directory cleanup returned errors.");
            Check.False(Directory.Exists(nested), "Empty nested directory was not cleaned.");
            Check.True(Directory.Exists(mediaRoot), "Allowed root itself must never be deleted.");
            Check.True(File.Exists(strm), "STRM source must remain until Emby deletes the item.");
        });
    }

    private static void ParseIntroDbAppCanonicalPayload()
    {
        const string json = "{\"imdb_id\":\"tt0903747\",\"season\":1,\"episode\":1,\"intro\":null,\"recap\":null,\"outro\":{\"start_sec\":3431,\"end_sec\":3500,\"start_ms\":3431000,\"end_ms\":3500000,\"confidence\":1,\"submission_count\":1}}";
        var doc = UnifiedIntroDbRawParser.ParseIntroDbSegments(json);
        Check.NotNull(doc, "Parser returned null for canonical IntroDB.app payload.");
        Check.False(doc.IntroStartSeconds.HasValue, "Payload has intro=null but parser invented an intro start.");
        Check.False(doc.IntroEndSeconds.HasValue, "Payload has intro=null but parser invented an intro end.");
        Check.Near(3431d, doc.CreditsStartSeconds, 0.0001, "Credits start mismatch.");
        Check.Near(3500d, doc.CreditsEndSeconds, 0.0001, "Credits end mismatch.");
        Check.Near(1d, doc.CreditsConfidence, 0.0001, "Credits confidence mismatch.");
        Check.Equal("tt0903747", doc.ExternalId, "External id mismatch.");
    }

    private static void ParseTheIntroDbCanonicalPayload()
    {
        const string json = "{\"tmdb_id\":1396,\"type\":\"tv\",\"season\":1,\"episode\":1,\"intro\":[{\"start_ms\":228892,\"end_ms\":245607}],\"credits\":[{\"start_ms\":3431000,\"end_ms\":null}]}";
        var identity = new UnifiedIntroDbIdentity { SeriesTmdbId = "1396", SeasonNumber = 1, EpisodeNumber = 1, DurationMs = 3486591 };
        var doc = UnifiedIntroDbRawParser.ParseTheIntroDb(json, identity, "v3");
        Check.NotNull(doc, "Parser returned null for canonical TheIntroDB payload.");
        Check.Near(228.892d, doc.IntroStartSeconds, 0.0001, "Intro start mismatch.");
        Check.Near(245.607d, doc.IntroEndSeconds, 0.0001, "Intro end mismatch.");
        Check.Near(3431d, doc.CreditsStartSeconds, 0.0001, "Credits start mismatch.");
        Check.False(doc.CreditsEndSeconds.HasValue, "Null credits end must remain null.");
        Check.Equal("TheIntroDB.org v3", doc.Source, "Source mismatch.");
        Check.Equal("1396", doc.ExternalId, "External id mismatch.");
    }

    private static void MergeIntroProviders()
    {
        const string introDb = "{\"imdb_id\":\"tt0903747\",\"intro\":null,\"outro\":{\"start_sec\":3431,\"end_sec\":3500,\"confidence\":1}}";
        const string theIntroDb = "{\"tmdb_id\":1396,\"intro\":[{\"start_ms\":228892,\"end_ms\":245607}],\"credits\":[{\"start_ms\":3431000,\"end_ms\":null}]}";
        var identity = new UnifiedIntroDbIdentity { SeriesTmdbId = "1396", SeasonNumber = 1, EpisodeNumber = 1 };
        var first = UnifiedIntroDbRawParser.ParseIntroDbSegments(introDb);
        var second = UnifiedIntroDbRawParser.ParseTheIntroDb(theIntroDb, identity, "v3");
        var merged = UnifiedIntroDbRawParser.MergePreferExisting(first, second);

        Check.Near(228.892d, merged.IntroStartSeconds, 0.0001, "Merged intro start mismatch.");
        Check.Near(245.607d, merged.IntroEndSeconds, 0.0001, "Merged intro end mismatch.");
        Check.Near(3431d, merged.CreditsStartSeconds, 0.0001, "Existing provider credits should be retained.");
        Check.Near(3500d, merged.CreditsEndSeconds, 0.0001, "Existing provider credits end should be retained.");
        Check.True(merged.Source.Contains("IntroDB.app", StringComparison.Ordinal) && merged.Source.Contains("TheIntroDB.org v3", StringComparison.Ordinal),
            "Merged source should identify both contributing providers.");
    }

    private static void IntroConfidenceNormalizesPercent()
    {
        const string json = "{\"imdb_id\":\"tt1\",\"intro\":{\"start_sec\":10,\"end_sec\":20,\"confidence\":87}}";
        var doc = UnifiedIntroDbRawParser.ParseIntroDbSegments(json);
        Check.Near(0.87d, doc.IntroConfidence, 0.0001, "87 percent confidence was not normalized to 0.87.");
    }

    private static void BuildCustomIntroDbUrlEscapesValues()
    {
        var identity = new UnifiedIntroDbIdentity
        {
            SeriesTmdbId = "1396",
            SeriesImdbId = "tt0903747",
            EpisodeTmdbId = "62085",
            EpisodeImdbId = "tt0959621",
            SeasonNumber = 1,
            EpisodeNumber = 1,
            SeriesName = "Breaking Bad / 绝命毒师",
            EpisodeName = "Pilot & test",
            DurationMs = 3486591
        };
        var url = UnifiedIntroDbBridge.BuildUrl(
            "https://example.invalid/api?tmdb={series_tmdb}&s={season}&e={episode}&series={series_name}&name={episode_name}&duration={duration_ms}",
            identity, out var error);
        Check.True(string.IsNullOrEmpty(error), "Unexpected BuildUrl error: " + error);
        Check.NotNull(url, "BuildUrl returned null.");
        Check.True(url.Contains("tmdb=1396", StringComparison.Ordinal), "TMDB placeholder not replaced.");
        Check.True(url.Contains("series=Breaking%20Bad%20%2F%20%E7%BB%9D%E5%91%BD%E6%AF%92%E5%B8%88", StringComparison.OrdinalIgnoreCase),
            "Series name was not URI escaped.");
        Check.True(url.Contains("name=Pilot%20%26%20test", StringComparison.OrdinalIgnoreCase), "Episode name was not URI escaped.");
        Check.True(url.Contains("duration=3486591", StringComparison.Ordinal), "Duration placeholder not replaced.");
    }

    private static void BuildCustomIntroDbUrlRejectsUnsafeScheme()
    {
        var identity = new UnifiedIntroDbIdentity { SeriesTmdbId = "1396", SeasonNumber = 1, EpisodeNumber = 1 };
        var url = UnifiedIntroDbBridge.BuildUrl("file:///tmp/{series_tmdb}/{season}/{episode}", identity, out var error);
        Check.True(url == null, "Non-http scheme should be rejected.");
        Check.True(error?.Contains("HTTP/HTTPS", StringComparison.OrdinalIgnoreCase) == true, "Expected HTTP/HTTPS validation error.");
    }

    private static void RemotePathNormalization()
    {
        Check.Equal("/115/movie/file.mkv", RemoteDeepDeleteRuntimeSettings.NormalizeRemotePath("//115/series/../movie//./file.mkv"),
            "Remote path normalization mismatch.");
        Check.True(RemoteDeepDeleteRuntimeSettings.NormalizeRemotePath("../../etc/passwd") == null,
            "Root-escaping path traversal must be rejected.");
    }

    private static void RemoteAllowedRootBoundary()
    {
        var roots = RemoteDeepDeleteRuntimeSettings.ParseAllowedRoots("/115\n/Movies/HD");
        Check.True(RemoteDeepDeleteRuntimeSettings.IsWithinAllowedRoot("/115/movie.mkv", roots), "Child path should be allowed.");
        Check.True(RemoteDeepDeleteRuntimeSettings.IsWithinAllowedRoot("/115", roots), "Exact root should be allowed.");
        Check.False(RemoteDeepDeleteRuntimeSettings.IsWithinAllowedRoot("/115abc/movie.mkv", roots),
            "Textual prefix without path boundary must not be allowed.");
    }

    private static void RemoteMappingLongestPrefixFirst()
    {
        var mappings = RemoteDeepDeleteRuntimeSettings.ParseMappings(
            "https://example.invalid/d => /root\nhttps://example.invalid/d/115 => /root/115");
        Check.Equal(2, mappings.Count, "Mapping count mismatch.");
        Check.Equal("https://example.invalid/d/115", mappings[0].SourcePrefix, "Longest source prefix should be first.");
    }

    private static void RemotePlanSafetyAcceptsChildPath()
    {
        var plan = new RemoteDeepDeletePlan
        {
            Allowed = true,
            SourceTarget = "https://alist.example.com/d/115/%E7%94%B5%E5%BD%B1/movie.mkv",
            MatchedSourcePrefix = "https://alist.example.com/d/115"
        };
        RemoteDeepDeletePlanSafetyPatches.Postfix(ref plan);
        Check.True(plan.Allowed, "Valid child URI path was rejected: " + plan.Error);
    }

    private static void RemotePlanSafetyRejectsPrefixCollision()
    {
        var plan = new RemoteDeepDeletePlan
        {
            Allowed = true,
            SourceTarget = "https://alist.example.com/d/115abc/movie.mkv",
            MatchedSourcePrefix = "https://alist.example.com/d/115"
        };
        RemoteDeepDeletePlanSafetyPatches.Postfix(ref plan);
        Check.False(plan.Allowed, "Textual prefix collision was accepted.");
        Check.True(plan.Error?.Contains("path-segment boundary", StringComparison.OrdinalIgnoreCase) == true,
            "Expected path-boundary rejection reason.");
    }

    private static void RemotePlanSafetyRejectsAuthorityMismatch()
    {
        var plan = new RemoteDeepDeletePlan
        {
            Allowed = true,
            SourceTarget = "https://evil.example.com/d/115/movie.mkv",
            MatchedSourcePrefix = "https://alist.example.com/d/115"
        };
        RemoteDeepDeletePlanSafetyPatches.Postfix(ref plan);
        Check.False(plan.Allowed, "Authority mismatch was accepted.");
        Check.True(plan.Error?.Contains("authority", StringComparison.OrdinalIgnoreCase) == true,
            "Expected authority rejection reason.");
    }

    private static void ProbeSafetyStructuredSuccessWins()
    {
        var original = new RemoteDeepDeleteProbeResult
        {
            Provider = RemoteDeepDeleteProviderType.OpenList.ToString(),
            HttpStatusCode = 200,
            ApiCode = 200,
            Success = true,
            Missing = true,
            Exists = false,
            Error = "fuzzy not found"
        };
        var normalized = NormalizeProbe(original);
        Check.True(normalized.Success, "Structured successful probe should remain successful.");
        Check.True(normalized.Exists, "HTTP 200/API 200 must be normalized to Exists.");
        Check.False(normalized.Missing, "HTTP 200/API 200 cannot authorize Missing.");
        Check.True(normalized.Error == null, "Structured success should clear fuzzy missing error.");
    }

    private static void ProbeSafetyRejectsAuthorizationFailure()
    {
        var original = new RemoteDeepDeleteProbeResult
        {
            Provider = RemoteDeepDeleteProviderType.OpenList.ToString(),
            HttpStatusCode = 401,
            ApiCode = 401,
            Success = true,
            Missing = true
        };
        var normalized = NormalizeProbe(original);
        Check.False(normalized.Success, "Authorization failure cannot be successful.");
        Check.False(normalized.Missing, "Authorization failure cannot prove missing.");
        Check.False(normalized.Exists, "Authorization failure cannot prove exists.");
        Check.True(normalized.Error?.Contains("authorization", StringComparison.OrdinalIgnoreCase) == true,
            "Expected authorization error.");
    }

    private static void ProbeSafetyRejectsBackendFailure()
    {
        var original = new RemoteDeepDeleteProbeResult
        {
            Provider = RemoteDeepDeleteProviderType.OpenList.ToString(),
            HttpStatusCode = 500,
            ApiCode = 500,
            Success = true,
            Missing = true
        };
        var normalized = NormalizeProbe(original);
        Check.False(normalized.Success, "HTTP 500 cannot be successful.");
        Check.False(normalized.Missing, "HTTP 500 cannot prove missing.");
        Check.True(normalized.Error?.Contains("non-success HTTP 500", StringComparison.OrdinalIgnoreCase) == true,
            "Expected backend failure rejection reason.");
    }

    private static RemoteDeepDeleteProbeResult NormalizeProbe(RemoteDeepDeleteProbeResult result)
    {
        Task<RemoteDeepDeleteProbeResult> task = Task.FromResult(result);
        RemoteDeepDeleteProbeSafetyPatches.Postfix(ref task);
        return task.GetAwaiter().GetResult();
    }

    private static ExperienceEnhanceOptions LocalDeleteOptions(string allowedRoot, bool dryRun, bool associated)
    {
        return new ExperienceEnhanceOptions
        {
            EnableDeepDelete = true,
            DeepDeleteDryRun = dryRun,
            DeepDeleteAllowedRoots = allowedRoot,
            DeepDeleteTargetFile = true,
            DeepDeleteAssociatedFiles = associated,
            DeepDeleteEmptyDirectories = false
        };
    }

    private static void WithTempTree(Action<string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "strmassistant-contract-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        try
        {
            body(root);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    private static bool SamePath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    private static class Check
    {
        public static void True(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        public static void False(bool condition, string message) => True(!condition, message);

        public static void NotNull(object value, string message) => True(value != null, message);

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException($"{message} Expected={expected}, Actual={actual}");
        }

        public static void Near(double expected, double? actual, double tolerance, string message)
        {
            if (!actual.HasValue || Math.Abs(expected - actual.Value) > tolerance)
                throw new InvalidOperationException($"{message} Expected={expected}, Actual={(actual.HasValue ? actual.Value.ToString(CultureInfo.InvariantCulture) : "null")}");
        }

        public static void Empty<T>(ICollection<T> values, string message)
        {
            if (values == null || values.Count != 0)
                throw new InvalidOperationException($"{message} Count={values?.Count ?? -1}");
        }

        public static void ContainsPath(IEnumerable<string> paths, string expected)
        {
            if (!paths.Any(path => SamePath(path, expected)))
                throw new InvalidOperationException("Expected path was not present in plan: " + expected);
        }
    }
}

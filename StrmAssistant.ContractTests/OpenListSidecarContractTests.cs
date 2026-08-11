using StrmAssistant.Experience;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;

namespace StrmAssistant.ContractTests;

internal static class OpenListSidecarContractTests
{
    [ModuleInitializer]
    internal static void RunModuleTests()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("OpenList sidecar plan selects only conservative same-stem files", SidecarPlanSelectsConservativeCandidates),
            ("OpenList sidecar delete sends frozen candidates and verifies disappearance", SidecarDeleteAndVerify),
            ("OpenList sidecar plan refuses truncated directory listing", SidecarPlanRejectsTruncatedListing),
            ("OpenList sidecar plan refuses more than 64 candidates", SidecarPlanRejectsTooManyCandidates),
            ("OpenList sidecar API-level remove failure does not report success", SidecarRemoveApiFailure),
            ("OpenList sidecar verification detects remaining candidate", SidecarVerificationDetectsRemaining)
        };

        var failures = new List<string>();
        Console.WriteLine($"StrmAssistant OpenList sidecar contract tests: {tests.Length} cases");
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
            throw new InvalidOperationException("OpenList sidecar contract failures: " + string.Join(" | ", failures));
    }

    private static void SidecarPlanSelectsConservativeCandidates()
    {
        using var transport = FakeSidecarTransport.Default();
        Configure();
        var plan = new OpenListRemoteSidecarService().PlanAsync(MainPlan(), CancellationToken.None)
            .GetAwaiter().GetResult();
        AssertTrue(plan.Success, "Sidecar plan failed: " + plan.Error);
        AssertSequence(plan.Candidates, "movie-poster.jpg", "movie.en.ass", "movie.srt");
        AssertFalse(plan.Candidates.Contains("poster.jpg"), "Generic poster.jpg must never be inferred as a sidecar.");
        AssertFalse(plan.Candidates.Contains("movie.1080p.mkv"), "Another video version must never be a sidecar candidate.");
        AssertFalse(plan.Candidates.Contains("other.srt"), "Unrelated subtitle was selected.");
        AssertFalse(plan.Candidates.Contains("movie.nfo"), "Directory entry was selected as a file sidecar.");
        AssertEqual("sidecar-token", transport.LastAuthorization, "Sidecar listing Authorization mismatch.");
    }

    private static void SidecarDeleteAndVerify()
    {
        using var transport = FakeSidecarTransport.Default();
        Configure();
        var service = new OpenListRemoteSidecarService();
        var plan = service.PlanAsync(MainPlan(), CancellationToken.None).GetAwaiter().GetResult();
        var result = service.DeleteAndVerifyAsync(MainPlan(), plan, CancellationToken.None)
            .GetAwaiter().GetResult();
        AssertTrue(result.Success, "Verified sidecar delete failed: " + result.Error);
        AssertTrue(result.Executed, "Sidecar remove request was not executed.");
        AssertEqual(2, transport.ListCalls, "Expected plan listing plus verification listing.");
        AssertEqual(1, transport.RemoveCalls, "Expected exactly one sidecar remove request.");
        AssertSequence(result.RequestedNames, "movie-poster.jpg", "movie.en.ass", "movie.srt");
        AssertTrue(result.RemainingNames.Count == 0, "Deleted sidecars remain after verification.");
        AssertContains(transport.LastRemoveBody, "\"movie.srt\"", "Remove body missing subtitle candidate.");
        AssertContains(transport.LastRemoveBody, "\"movie.en.ass\"", "Remove body missing language subtitle candidate.");
        AssertContains(transport.LastRemoveBody, "\"movie-poster.jpg\"", "Remove body missing same-stem image candidate.");
    }

    private static void SidecarPlanRejectsTruncatedListing()
    {
        using var transport = FakeSidecarTransport.Default(totalOverride: 1001);
        Configure();
        var plan = new OpenListRemoteSidecarService().PlanAsync(MainPlan(), CancellationToken.None)
            .GetAwaiter().GetResult();
        AssertFalse(plan.Success, "Truncated listing was accepted.");
        AssertTrue(plan.DirectoryListingTruncated, "Plan did not report listing truncation.");
        AssertTrue(plan.Error?.Contains("more than 1000", StringComparison.OrdinalIgnoreCase) == true,
            "Truncation error is not explicit: " + plan.Error);
    }

    private static void SidecarPlanRejectsTooManyCandidates()
    {
        var names = new List<Entry> { new Entry("movie.mkv", false) };
        for (var i = 0; i < 65; i++) names.Add(new Entry("movie.lang" + i + ".srt", false));
        using var transport = new FakeSidecarTransport(names, null, false, false);
        Configure();
        var plan = new OpenListRemoteSidecarService().PlanAsync(MainPlan(), CancellationToken.None)
            .GetAwaiter().GetResult();
        AssertFalse(plan.Success, "More than 64 sidecars were accepted.");
        AssertTrue(plan.Error?.Contains("More than 64", StringComparison.OrdinalIgnoreCase) == true,
            "Candidate limit error missing: " + plan.Error);
    }

    private static void SidecarRemoveApiFailure()
    {
        using var transport = FakeSidecarTransport.Default(removeApiFailure: true);
        Configure();
        var service = new OpenListRemoteSidecarService();
        var plan = service.PlanAsync(MainPlan(), CancellationToken.None).GetAwaiter().GetResult();
        var result = service.DeleteAndVerifyAsync(MainPlan(), plan, CancellationToken.None)
            .GetAwaiter().GetResult();
        AssertFalse(result.Success, "API code 500 remove was reported successful.");
        AssertEqual(500, result.ApiCode, "API failure code was not propagated.");
        AssertEqual(1, transport.RemoveCalls, "Expected one remove attempt.");
        AssertEqual(1, transport.ListCalls, "Verification listing must not run after rejected remove.");
    }

    private static void SidecarVerificationDetectsRemaining()
    {
        using var transport = FakeSidecarTransport.Default(keepOneAfterDelete: true);
        Configure();
        var service = new OpenListRemoteSidecarService();
        var plan = service.PlanAsync(MainPlan(), CancellationToken.None).GetAwaiter().GetResult();
        var result = service.DeleteAndVerifyAsync(MainPlan(), plan, CancellationToken.None)
            .GetAwaiter().GetResult();
        AssertFalse(result.Success, "Verification accepted a remaining sidecar.");
        AssertTrue(result.RemainingNames.Count == 1, "Expected exactly one intentionally retained candidate.");
        AssertContains(result.Error, result.RemainingNames[0], "Verification error should identify the remaining file.");
    }

    private static void Configure()
    {
        RemoteDeepDeleteRuntimeSettings.Save(new RemoteDeepDeleteOptions
        {
            Enabled = true,
            Provider = RemoteDeepDeleteProviderType.OpenList,
            BaseUrl = "https://fake.openlist.test",
            AccessToken = "sidecar-token",
            TimeoutSeconds = 5,
            TreatNotFoundAsSuccess = true,
            DeleteAssociatedSidecars = true,
            AllowedRemoteRoots = "/115"
        });
    }

    private static RemoteDeepDeletePlan MainPlan()
    {
        return new RemoteDeepDeletePlan
        {
            Applicable = true,
            Allowed = true,
            Provider = RemoteDeepDeleteProviderType.OpenList.ToString(),
            RemotePath = "/115/movie.mkv",
            RemoteDirectory = "/115",
            RemoteName = "movie.mkv"
        };
    }

    private sealed class Entry
    {
        public Entry(string name, bool directory)
        {
            Name = name;
            Directory = directory;
        }
        public string Name { get; }
        public bool Directory { get; }
    }

    private sealed class FakeSidecarTransport : IDisposable
    {
        private readonly List<Entry> _entries;
        private readonly long? _totalOverride;
        private readonly bool _removeApiFailure;
        private readonly bool _keepOneAfterDelete;
        public int ListCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public string LastAuthorization { get; private set; }
        public string LastRemoveBody { get; private set; }

        public FakeSidecarTransport(IEnumerable<Entry> entries, long? totalOverride,
            bool removeApiFailure, bool keepOneAfterDelete)
        {
            _entries = entries.ToList();
            _totalOverride = totalOverride;
            _removeApiFailure = removeApiFailure;
            _keepOneAfterDelete = keepOneAfterDelete;
            OpenListRemoteSidecarService.SendAsyncOverride = SendAsync;
        }

        public static FakeSidecarTransport Default(long? totalOverride = null,
            bool removeApiFailure = false, bool keepOneAfterDelete = false)
        {
            return new FakeSidecarTransport(new[]
            {
                new Entry("movie.mkv", false),
                new Entry("movie.srt", false),
                new Entry("movie.en.ass", false),
                new Entry("movie-poster.jpg", false),
                new Entry("poster.jpg", false),
                new Entry("movie.1080p.mkv", false),
                new Entry("other.srt", false),
                new Entry("movie.nfo", true),
                new Entry("../movie.xml", false)
            }, totalOverride, removeApiFailure, keepOneAfterDelete);
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            HttpCompletionOption completionOption, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastAuthorization = request.Headers.TryGetValues("Authorization", out var values)
                ? values.FirstOrDefault()
                : null;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = request.Content == null ? string.Empty :
                await request.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (path == "/api/fs/list")
            {
                ListCalls++;
                return Response(HttpStatusCode.OK, BuildListJson());
            }
            if (path == "/api/fs/remove")
            {
                RemoveCalls++;
                LastRemoveBody = body;
                if (_removeApiFailure)
                    return Response(HttpStatusCode.OK, "{\"code\":500,\"message\":\"backend refused\"}");

                var requested = _entries.Where(entry => !entry.Directory &&
                    body.Contains("\"" + entry.Name + "\"", StringComparison.Ordinal)).ToList();
                if (_keepOneAfterDelete && requested.Count > 0) requested.RemoveAt(0);
                foreach (var entry in requested) _entries.Remove(entry);
                return Response(HttpStatusCode.OK, "{\"code\":200,\"message\":\"success\"}");
            }
            return Response(HttpStatusCode.NotFound, "{\"code\":404}");
        }

        private string BuildListJson()
        {
            var total = _totalOverride ?? _entries.Count;
            var content = string.Join(",", _entries.Select(entry =>
                "{\"name\":" + Json(entry.Name) + ",\"is_dir\":" +
                (entry.Directory ? "true" : "false") + ",\"size\":1,\"type\":0}"));
            return "{\"code\":200,\"message\":\"success\",\"data\":{\"content\":[" +
                   content + "],\"total\":" + total + "}}";
        }

        private static string Json(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string json)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json")
            };
        }

        public void Dispose()
        {
            OpenListRemoteSidecarService.SendAsyncOverride = null;
        }
    }

    private static void AssertSequence(IReadOnlyList<string> actual, params string[] expected)
    {
        if (actual.Count != expected.Length || !actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidOperationException("Sequence mismatch. Expected=" + string.Join(",", expected) +
                                                " Actual=" + string.Join(",", actual));
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
        if (value?.Contains(expected, StringComparison.Ordinal) != true)
            throw new InvalidOperationException(message + " Actual=" + value);
    }
}

using StrmAssistant.Experience;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;

namespace StrmAssistant.ContractTests;

internal static class RemoteHttpContractTests
{
    [ModuleInitializer]
    internal static void RunModuleTests()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("OpenList HTTP transaction performs pre-probe remove and post-verify", OpenListDeleteRoundTrip),
            ("OpenList HTTP transaction sends configured Authorization and remove body", OpenListDeleteCarriesAuthAndBody),
            ("OpenList 401 containing not-found text must fail closed", OpenList401CannotMasqueradeAsMissing),
            ("OpenList 500 containing not-found text must fail closed", OpenList500CannotMasqueradeAsMissing),
            ("OpenList delete HTTP 500 cannot complete transaction even with fuzzy missing text", OpenListDelete500CannotComplete),
            ("OpenList HTTP 200 with API code 500 cannot be accepted as delete", OpenListDeleteApi500CannotComplete)
        };

        var failures = new List<string>();
        Console.WriteLine($"StrmAssistant remote HTTP contract tests: {tests.Length} cases");
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
            throw new InvalidOperationException("Remote HTTP contract failures: " + string.Join(" | ", failures));
    }

    private static void OpenListDeleteRoundTrip()
    {
        using var transport = new FakeOpenListTransport(FakeMode.Success);
        var result = Execute(transport);
        AssertTrue(result.Success, "Verified OpenList transaction should succeed: " + result.Error);
        AssertTrue(result.PreProbeVerifiedExists, "Pre-probe did not verify existence.");
        AssertTrue(result.DeleteAccepted, "Delete response was not accepted.");
        AssertTrue(result.VerifiedDeleted, "Post-delete probe did not verify missing.");
        AssertEqual(2, transport.GetCalls, "Expected exactly pre- and post-delete fs/get calls.");
        AssertEqual(1, transport.RemoveCalls, "Expected exactly one fs/remove call.");
        AssertFalse(transport.Exists, "Fake remote object still exists after successful transaction.");
    }

    private static void OpenListDeleteCarriesAuthAndBody()
    {
        using var transport = new FakeOpenListTransport(FakeMode.Success);
        var result = Execute(transport);
        AssertTrue(result.Success, "Transaction failed unexpectedly: " + result.Error);
        AssertEqual("test-openlist-token", transport.LastAuthorization, "Authorization header mismatch.");
        AssertContains(transport.LastRemoveBody, "\"dir\":\"/115\"", "Remove body did not contain the mapped directory.");
        AssertContains(transport.LastRemoveBody, "\"movie.mkv\"", "Remove body did not contain the mapped filename.");
    }

    private static void OpenList401CannotMasqueradeAsMissing()
    {
        using var transport = new FakeOpenListTransport(FakeMode.UnauthorizedProbeWithMissingText);
        var result = Execute(transport);
        AssertFalse(result.Success, "HTTP 401 with fuzzy not-found text was incorrectly accepted as success.");
        AssertEqual(0, transport.RemoveCalls, "A destructive remove request was sent after authorization failure.");
        AssertTrue(result.PreProbeError?.Contains("authorization", StringComparison.OrdinalIgnoreCase) == true,
            "Failure should explicitly identify authorization failure. Error=" + result.PreProbeError);
    }

    private static void OpenList500CannotMasqueradeAsMissing()
    {
        using var transport = new FakeOpenListTransport(FakeMode.BackendProbeWithMissingText);
        var result = Execute(transport);
        AssertFalse(result.Success, "HTTP 500 with fuzzy not-found text was incorrectly accepted as success.");
        AssertEqual(0, transport.RemoveCalls, "A destructive remove request was sent after backend probe failure.");
        AssertEqual(500, result.PreProbeStatusCode, "Backend failure status code was not propagated.");
    }

    private static void OpenListDelete500CannotComplete()
    {
        using var transport = new FakeOpenListTransport(FakeMode.DeleteHttp500WithMissingText);
        var result = Execute(transport);
        AssertFalse(result.Success, "HTTP 500 delete response completed the transaction.");
        AssertEqual(1, transport.RemoveCalls, "Expected one attempted remove request.");
        AssertFalse(result.DeleteAccepted, "HTTP 500 delete response must not be accepted because its body says not found.");
        AssertTrue(transport.Exists, "Fake object should remain after failed delete.");
    }

    private static void OpenListDeleteApi500CannotComplete()
    {
        using var transport = new FakeOpenListTransport(FakeMode.DeleteApi500);
        var result = Execute(transport);
        AssertFalse(result.Success, "HTTP 200/API 500 delete response completed the transaction.");
        AssertFalse(result.DeleteAccepted, "HTTP 200/API 500 must not be marked DeleteAccepted.");
        AssertTrue(transport.Exists, "Fake object should remain after API-level delete failure.");
    }

    private static RemoteDeepDeleteExecutionResult Execute(FakeOpenListTransport transport)
    {
        RemoteDeepDeleteRuntimeSettings.Save(new RemoteDeepDeleteOptions
        {
            Enabled = true,
            Provider = RemoteDeepDeleteProviderType.OpenList,
            BaseUrl = "https://fake.openlist.test",
            AccessToken = "test-openlist-token",
            TimeoutSeconds = 5,
            TreatNotFoundAsSuccess = true,
            AllowedRemoteRoots = "/115",
            PathMappings = "https://unused.invalid/d/115 => /115"
        });

        var plan = new RemoteDeepDeletePlan
        {
            Applicable = true,
            Allowed = true,
            TargetLooksRemote = true,
            Provider = RemoteDeepDeleteProviderType.OpenList.ToString(),
            RemotePath = "/115/movie.mkv",
            RemoteDirectory = "/115",
            RemoteName = "movie.mkv",
            EndpointHost = "https://fake.openlist.test"
        };
        return new RemoteDeepDeleteService().ExecuteAsync(plan, CancellationToken.None).GetAwaiter().GetResult();
    }

    private enum FakeMode
    {
        Success,
        UnauthorizedProbeWithMissingText,
        BackendProbeWithMissingText,
        DeleteHttp500WithMissingText,
        DeleteApi500
    }

    private sealed class FakeOpenListTransport : IDisposable
    {
        private readonly FakeMode _mode;
        private int _getCalls;
        private int _removeCalls;
        private bool _exists = true;

        public FakeOpenListTransport(FakeMode mode)
        {
            _mode = mode;
            RemoteDeepDeleteService.SendAsyncOverride = SendAsync;
        }

        public int GetCalls => _getCalls;
        public int RemoveCalls => _removeCalls;
        public bool Exists => _exists;
        public string LastAuthorization { get; private set; }
        public string LastRemoveBody { get; private set; }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            HttpCompletionOption completionOption, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastAuthorization = request.Headers.TryGetValues("Authorization", out var values)
                ? values.FirstOrDefault()
                : null;
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync().ConfigureAwait(false);
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path == "/api/fs/get")
            {
                _getCalls++;
                if (_mode == FakeMode.UnauthorizedProbeWithMissingText)
                    return Response(HttpStatusCode.Unauthorized,
                        "{\"code\":401,\"message\":\"object not found\"}");
                if (_mode == FakeMode.BackendProbeWithMissingText)
                    return Response(HttpStatusCode.InternalServerError,
                        "{\"code\":500,\"message\":\"file not found\"}");
                return _exists
                    ? Response(HttpStatusCode.OK, "{\"code\":200,\"data\":{\"name\":\"movie.mkv\"}}")
                    : Response(HttpStatusCode.NotFound, "{\"code\":404,\"message\":\"object not found\"}");
            }

            if (path == "/api/fs/remove")
            {
                _removeCalls++;
                LastRemoveBody = body;
                if (_mode == FakeMode.DeleteHttp500WithMissingText)
                    return Response(HttpStatusCode.InternalServerError,
                        "{\"code\":500,\"message\":\"file not found\"}");
                if (_mode == FakeMode.DeleteApi500)
                    return Response(HttpStatusCode.OK,
                        "{\"code\":500,\"message\":\"backend refused delete\"}");
                _exists = false;
                return Response(HttpStatusCode.OK, "{\"code\":200,\"message\":\"success\"}");
            }

            return Response(HttpStatusCode.NotFound, "{\"code\":404}");
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
            if (ReferenceEquals(RemoteDeepDeleteService.SendAsyncOverride?.Target, this))
                RemoteDeepDeleteService.SendAsyncOverride = null;
            else
                RemoteDeepDeleteService.SendAsyncOverride = null;
        }
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

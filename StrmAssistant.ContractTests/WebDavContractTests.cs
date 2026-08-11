using StrmAssistant.Experience;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;

namespace StrmAssistant.ContractTests;

internal static class WebDavContractTests
{
    [ModuleInitializer]
    internal static void RunModuleTests()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("WebDAV transaction performs HEAD DELETE HEAD verification", WebDavRoundTrip),
            ("WebDAV transaction sends Basic authorization and escaped path", WebDavCarriesAuthAndEscapedPath),
            ("WebDAV HEAD 405 falls back to PROPFIND Depth zero", WebDavFallsBackToPropFind),
            ("WebDAV 401 pre-probe blocks destructive DELETE", WebDav401BlocksDelete),
            ("WebDAV DELETE 500 does not complete transaction", WebDavDelete500Fails),
            ("WebDAV already-missing succeeds only when configured", WebDavAlreadyMissingHonorsOption)
        };

        var failures = new List<string>();
        Console.WriteLine($"StrmAssistant WebDAV contract tests: {tests.Length} cases");
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
            throw new InvalidOperationException("WebDAV contract failures: " + string.Join(" | ", failures));
    }

    private static void WebDavRoundTrip()
    {
        using var transport = new FakeWebDavTransport(WebDavMode.Success);
        var result = Execute(transport, true);
        AssertTrue(result.Success, "Verified WebDAV transaction should succeed: " + result.Error);
        AssertTrue(result.PreProbeVerifiedExists, "Pre-probe did not confirm existence.");
        AssertTrue(result.DeleteAccepted, "DELETE was not accepted.");
        AssertTrue(result.VerifiedDeleted, "Post-delete verification did not confirm missing.");
        AssertSequence(transport.Methods, "HEAD", "DELETE", "HEAD");
        AssertFalse(transport.Exists, "Fake WebDAV object still exists.");
    }

    private static void WebDavCarriesAuthAndEscapedPath()
    {
        using var transport = new FakeWebDavTransport(WebDavMode.Success);
        var result = Execute(transport, true);
        AssertTrue(result.Success, "WebDAV transaction failed: " + result.Error);
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:p@ss"));
        AssertEqual(expected, transport.LastAuthorization, "Basic Authorization mismatch.");
        AssertTrue(transport.Paths.All(path => path.Contains("/115/My%20Movie.mkv", StringComparison.Ordinal)),
            "WebDAV target path was not escaped consistently: " + string.Join(",", transport.Paths));
    }

    private static void WebDavFallsBackToPropFind()
    {
        using var transport = new FakeWebDavTransport(WebDavMode.HeadNotAllowed);
        var result = Execute(transport, true);
        AssertTrue(result.Success, "PROPFIND fallback transaction should succeed: " + result.Error);
        AssertSequence(transport.Methods, "HEAD", "PROPFIND", "DELETE", "HEAD", "PROPFIND");
        AssertTrue(transport.PropFindDepthHeaders.All(value => value == "0"),
            "PROPFIND requests must carry Depth: 0.");
    }

    private static void WebDav401BlocksDelete()
    {
        using var transport = new FakeWebDavTransport(WebDavMode.UnauthorizedProbe);
        var result = Execute(transport, true);
        AssertFalse(result.Success, "401 pre-probe was accepted.");
        AssertFalse(transport.Methods.Contains("DELETE"), "DELETE was sent after a 401 pre-probe.");
        AssertEqual(401, result.PreProbeStatusCode, "401 status was not propagated.");
    }

    private static void WebDavDelete500Fails()
    {
        using var transport = new FakeWebDavTransport(WebDavMode.Delete500);
        var result = Execute(transport, true);
        AssertFalse(result.Success, "DELETE 500 completed the transaction.");
        AssertFalse(result.DeleteAccepted, "DELETE 500 was marked accepted.");
        AssertTrue(transport.Exists, "Object changed state after failed DELETE.");
    }

    private static void WebDavAlreadyMissingHonorsOption()
    {
        using (var transport = new FakeWebDavTransport(WebDavMode.AlreadyMissing))
        {
            var result = Execute(transport, true);
            AssertTrue(result.Success && result.AlreadyMissing && result.VerifiedDeleted,
                "TreatNotFoundAsSuccess=true did not accept verified pre-existing missing state.");
            AssertFalse(transport.Methods.Contains("DELETE"), "DELETE should not be sent for already-missing target.");
        }
        using (var transport = new FakeWebDavTransport(WebDavMode.AlreadyMissing))
        {
            var result = Execute(transport, false);
            AssertFalse(result.Success, "TreatNotFoundAsSuccess=false accepted already-missing target.");
            AssertFalse(transport.Methods.Contains("DELETE"), "DELETE should not be sent when pre-probe is already missing.");
        }
    }

    private static RemoteDeepDeleteExecutionResult Execute(FakeWebDavTransport transport, bool treatMissing)
    {
        RemoteDeepDeleteRuntimeSettings.Save(new RemoteDeepDeleteOptions
        {
            Enabled = true,
            Provider = RemoteDeepDeleteProviderType.WebDav,
            BaseUrl = "https://fake.webdav.test",
            Username = "alice",
            Password = "p@ss",
            TimeoutSeconds = 5,
            TreatNotFoundAsSuccess = treatMissing,
            AllowedRemoteRoots = "/115"
        });
        var plan = new RemoteDeepDeletePlan
        {
            Applicable = true,
            Allowed = true,
            TargetLooksRemote = true,
            Provider = RemoteDeepDeleteProviderType.WebDav.ToString(),
            RemotePath = "/115/My Movie.mkv",
            RemoteDirectory = "/115",
            RemoteName = "My Movie.mkv"
        };
        return new RemoteDeepDeleteService().ExecuteAsync(plan, CancellationToken.None).GetAwaiter().GetResult();
    }

    private enum WebDavMode
    {
        Success,
        HeadNotAllowed,
        UnauthorizedProbe,
        Delete500,
        AlreadyMissing
    }

    private sealed class FakeWebDavTransport : IDisposable
    {
        private readonly WebDavMode _mode;
        public bool Exists { get; private set; } = true;
        public List<string> Methods { get; } = new List<string>();
        public List<string> Paths { get; } = new List<string>();
        public List<string> PropFindDepthHeaders { get; } = new List<string>();
        public string LastAuthorization { get; private set; }

        public FakeWebDavTransport(WebDavMode mode)
        {
            _mode = mode;
            if (mode == WebDavMode.AlreadyMissing) Exists = false;
            RemoteDeepDeleteService.SendAsyncOverride = SendAsync;
        }

        private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            HttpCompletionOption completionOption, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var method = request.Method.Method;
            Methods.Add(method);
            Paths.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
            LastAuthorization = request.Headers.Authorization?.ToString();
            if (method == "PROPFIND")
            {
                PropFindDepthHeaders.Add(request.Headers.TryGetValues("Depth", out var values)
                    ? values.FirstOrDefault()
                    : null);
            }

            if (_mode == WebDavMode.UnauthorizedProbe && (method == "HEAD" || method == "PROPFIND"))
                return Task.FromResult(Response(HttpStatusCode.Unauthorized));

            if (_mode == WebDavMode.HeadNotAllowed && method == "HEAD")
                return Task.FromResult(Response(HttpStatusCode.MethodNotAllowed));

            if (method == "HEAD" || method == "PROPFIND")
            {
                if (!Exists) return Task.FromResult(Response(HttpStatusCode.NotFound));
                return Task.FromResult(Response(method == "PROPFIND"
                    ? (HttpStatusCode)207
                    : HttpStatusCode.OK));
            }

            if (method == "DELETE")
            {
                if (_mode == WebDavMode.Delete500)
                    return Task.FromResult(Response(HttpStatusCode.InternalServerError));
                Exists = false;
                return Task.FromResult(Response(HttpStatusCode.NoContent));
            }

            return Task.FromResult(Response(HttpStatusCode.BadRequest));
        }

        private static HttpResponseMessage Response(HttpStatusCode status)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(string.Empty)
            };
        }

        public void Dispose()
        {
            RemoteDeepDeleteService.SendAsyncOverride = null;
        }
    }

    private static void AssertSequence(IReadOnlyList<string> actual, params string[] expected)
    {
        if (actual.Count != expected.Length || !actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidOperationException("Method sequence mismatch. Expected=" +
                                                string.Join(",", expected) + " Actual=" + string.Join(",", actual));
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

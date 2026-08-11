using StrmAssistant.Experience;
using System.Net;
using System.Net.Sockets;
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
            ("OpenList HTTP integration performs pre-probe remove and post-verify", OpenListDeleteRoundTrip),
            ("OpenList HTTP integration sends configured Authorization and remove body", OpenListDeleteCarriesAuthAndBody),
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
        using var server = new FakeOpenListServer(FakeMode.Success);
        var result = Execute(server);
        AssertTrue(result.Success, "Verified OpenList transaction should succeed: " + result.Error);
        AssertTrue(result.PreProbeVerifiedExists, "Pre-probe did not verify existence.");
        AssertTrue(result.DeleteAccepted, "Delete response was not accepted.");
        AssertTrue(result.VerifiedDeleted, "Post-delete probe did not verify missing.");
        AssertEqual(2, server.GetCalls, "Expected exactly pre- and post-delete fs/get calls.");
        AssertEqual(1, server.RemoveCalls, "Expected exactly one fs/remove call.");
        AssertFalse(server.Exists, "Fake remote object still exists after successful transaction.");
    }

    private static void OpenListDeleteCarriesAuthAndBody()
    {
        using var server = new FakeOpenListServer(FakeMode.Success);
        var result = Execute(server);
        AssertTrue(result.Success, "Transaction failed unexpectedly: " + result.Error);
        AssertEqual("test-openlist-token", server.LastAuthorization, "Authorization header mismatch.");
        AssertContains(server.LastRemoveBody, "\"dir\":\"/115\"", "Remove body did not contain the mapped directory.");
        AssertContains(server.LastRemoveBody, "\"movie.mkv\"", "Remove body did not contain the mapped filename.");
    }

    private static void OpenList401CannotMasqueradeAsMissing()
    {
        using var server = new FakeOpenListServer(FakeMode.UnauthorizedProbeWithMissingText);
        var result = Execute(server);
        AssertFalse(result.Success, "HTTP 401 with fuzzy not-found text was incorrectly accepted as success.");
        AssertEqual(0, server.RemoveCalls, "A destructive remove request was sent after authorization failure.");
        AssertTrue(result.Error?.Contains("probe", StringComparison.OrdinalIgnoreCase) == true ||
                   result.PreProbeError?.Contains("401", StringComparison.OrdinalIgnoreCase) == true,
            "Failure should identify the pre-delete probe/authentication problem. Error=" + result.Error);
    }

    private static void OpenList500CannotMasqueradeAsMissing()
    {
        using var server = new FakeOpenListServer(FakeMode.BackendProbeWithMissingText);
        var result = Execute(server);
        AssertFalse(result.Success, "HTTP 500 with fuzzy not-found text was incorrectly accepted as success.");
        AssertEqual(0, server.RemoveCalls, "A destructive remove request was sent after backend probe failure.");
    }

    private static void OpenListDelete500CannotComplete()
    {
        using var server = new FakeOpenListServer(FakeMode.DeleteHttp500WithMissingText);
        var result = Execute(server);
        AssertFalse(result.Success, "HTTP 500 delete response completed the transaction.");
        AssertEqual(1, server.RemoveCalls, "Expected one attempted remove request.");
        AssertTrue(server.Exists, "Fake object should remain after failed delete.");
    }

    private static void OpenListDeleteApi500CannotComplete()
    {
        using var server = new FakeOpenListServer(FakeMode.DeleteApi500);
        var result = Execute(server);
        AssertFalse(result.Success, "HTTP 200/API 500 delete response completed the transaction.");
        AssertFalse(result.DeleteAccepted, "HTTP 200/API 500 must not be marked DeleteAccepted.");
        AssertTrue(server.Exists, "Fake object should remain after API-level delete failure.");
    }

    private static RemoteDeepDeleteExecutionResult Execute(FakeOpenListServer server)
    {
        RemoteDeepDeleteRuntimeSettings.Save(new RemoteDeepDeleteOptions
        {
            Enabled = true,
            Provider = RemoteDeepDeleteProviderType.OpenList,
            BaseUrl = server.BaseUrl,
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
            EndpointHost = server.BaseUrl
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

    private sealed class FakeOpenListServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _loop;
        private readonly FakeMode _mode;
        private int _getCalls;
        private int _removeCalls;
        private volatile bool _exists = true;

        public FakeOpenListServer(FakeMode mode)
        {
            _mode = mode;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            BaseUrl = "http://127.0.0.1:" + endpoint.Port;
            _loop = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl { get; }
        public int GetCalls => Volatile.Read(ref _getCalls);
        public int RemoveCalls => Volatile.Read(ref _removeCalls);
        public bool Exists => _exists;
        public string LastAuthorization { get; private set; }
        public string LastRemoveBody { get; private set; }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    await HandleAsync(client).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    client?.Dispose();
                    break;
                }
                catch (ObjectDisposedException)
                {
                    client?.Dispose();
                    break;
                }
                catch
                {
                    client?.Dispose();
                    if (_cts.IsCancellationRequested) break;
                }
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var request = await ReadRequestAsync(stream).ConfigureAwait(false);
                if (request == null)
                {
                    await WriteAsync(stream, 400, "{\"code\":400}").ConfigureAwait(false);
                    return;
                }

                request.Headers.TryGetValue("Authorization", out var authorization);
                LastAuthorization = authorization;

                if (request.Path == "/api/fs/get")
                {
                    Interlocked.Increment(ref _getCalls);
                    if (_mode == FakeMode.UnauthorizedProbeWithMissingText)
                    {
                        await WriteAsync(stream, 401, "{\"code\":401,\"message\":\"object not found\"}").ConfigureAwait(false);
                        return;
                    }
                    if (_mode == FakeMode.BackendProbeWithMissingText)
                    {
                        await WriteAsync(stream, 500, "{\"code\":500,\"message\":\"file not found\"}").ConfigureAwait(false);
                        return;
                    }
                    if (_exists)
                    {
                        await WriteAsync(stream, 200, "{\"code\":200,\"data\":{\"name\":\"movie.mkv\"}}").ConfigureAwait(false);
                        return;
                    }
                    await WriteAsync(stream, 404, "{\"code\":404,\"message\":\"object not found\"}").ConfigureAwait(false);
                    return;
                }

                if (request.Path == "/api/fs/remove")
                {
                    Interlocked.Increment(ref _removeCalls);
                    LastRemoveBody = request.Body;
                    if (_mode == FakeMode.DeleteHttp500WithMissingText)
                    {
                        await WriteAsync(stream, 500, "{\"code\":500,\"message\":\"file not found\"}").ConfigureAwait(false);
                        return;
                    }
                    if (_mode == FakeMode.DeleteApi500)
                    {
                        await WriteAsync(stream, 200, "{\"code\":500,\"message\":\"backend refused delete\"}").ConfigureAwait(false);
                        return;
                    }
                    _exists = false;
                    await WriteAsync(stream, 200, "{\"code\":200,\"message\":\"success\"}").ConfigureAwait(false);
                    return;
                }

                await WriteAsync(stream, 404, "{\"code\":404}").ConfigureAwait(false);
            }
        }

        private static async Task<Request> ReadRequestAsync(NetworkStream stream)
        {
            var headerBytes = new List<byte>(1024);
            var matched = 0;
            var marker = new byte[] { 13, 10, 13, 10 };
            while (headerBytes.Count < 64 * 1024)
            {
                var value = stream.ReadByte();
                if (value < 0) return null;
                var b = (byte)value;
                headerBytes.Add(b);
                if (b == marker[matched])
                {
                    matched++;
                    if (matched == marker.Length) break;
                }
                else
                {
                    matched = b == marker[0] ? 1 : 0;
                }
            }
            if (matched != marker.Length) return null;

            var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var index = line.IndexOf(':');
                if (index <= 0) continue;
                headers[line.Substring(0, index).Trim()] = line.Substring(index + 1).Trim();
            }
            var contentLength = headers.TryGetValue("Content-Length", out var lengthText) &&
                                int.TryParse(lengthText, out var parsed) ? parsed : 0;
            var bodyBytes = new byte[Math.Max(0, contentLength)];
            var offset = 0;
            while (offset < bodyBytes.Length)
            {
                var read = await stream.ReadAsync(bodyBytes.AsMemory(offset, bodyBytes.Length - offset)).ConfigureAwait(false);
                if (read <= 0) break;
                offset += read;
            }
            return new Request
            {
                Path = requestLine[1].Split('?')[0],
                Headers = headers,
                Body = Encoding.UTF8.GetString(bodyBytes, 0, offset)
            };
        }

        private static async Task WriteAsync(NetworkStream stream, int status, string body)
        {
            var reason = status switch
            {
                200 => "OK",
                400 => "Bad Request",
                401 => "Unauthorized",
                404 => "Not Found",
                500 => "Internal Server Error",
                _ => "Status"
            };
            var payload = Encoding.UTF8.GetBytes(body ?? string.Empty);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header).ConfigureAwait(false);
            await stream.WriteAsync(payload).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }

        private sealed class Request
        {
            public string Path { get; set; }
            public Dictionary<string, string> Headers { get; set; }
            public string Body { get; set; }
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

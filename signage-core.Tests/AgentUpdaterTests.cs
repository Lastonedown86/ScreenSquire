using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using PiSignage.Signage;

namespace signage_core.Tests;

public class AgentUpdaterTests
{
    [Fact]
    public void ParseVersion_reads_the_constant()
    {
        var src = "PORT = 8080\nAGENT_VERSION = \"2026.07.24.1\"\napp = None\n";
        Assert.Equal("2026.07.24.1", AgentUpdater.ParseVersion(src));
    }

    [Fact]
    public void ParseVersion_returns_null_when_missing()
    {
        Assert.Null(AgentUpdater.ParseVersion("PORT = 8080\n"));
    }

    [Theory]
    // bundled,        installed,       expected
    [InlineData("2026.07.26.8", "2026.07.26.7", true)]   // ordinary update
    [InlineData("2026.07.26.7", "2026.07.26.8", false)]  // Pi is ahead — never downgrade
    [InlineData("2026.07.26.7", "2026.07.26.7", false)]  // already current
    [InlineData("2026.08.01.1", "2026.07.26.9", true)]   // N resets on a new day
    [InlineData("2026.07.26.10", "2026.07.26.9", true)]  // numeric, not lexical
    [InlineData("2026.07.26.7", "2026.7.26.7", false)]   // padding is not significance
    [InlineData("2026.07.26.7", null, true)]             // agent predates AGENT_VERSION
    [InlineData("2026.07.26.7", "", true)]
    [InlineData("2026.07.26.7", "not-a-version", true)]
    [InlineData(null, "2026.07.26.7", false)]            // no bundle to offer
    [InlineData("not-a-version", "2026.07.26.7", false)]
    [InlineData(null, null, false)]
    public void IsNewer_orders_versions_and_never_downgrades(
        string? bundled, string? installed, bool expected)
    {
        Assert.Equal(expected, AgentUpdater.IsNewer(bundled, installed));
    }

    [Fact]
    public void IsNewer_accepts_the_version_this_exe_actually_ships()
    {
        // Guards the format contract between agent/main.py and the app: if
        // AGENT_VERSION ever stops parsing, IsNewer silently answers "no update
        // available" for every Pi forever.
        var bundled = PiSignage.Control.AgentBundle.Version();
        Assert.NotNull(bundled);
        Assert.True(AgentUpdater.IsNewer(bundled, "0.0.0.1"));
        Assert.False(AgentUpdater.IsNewer(bundled, bundled));
    }

    [Fact]
    public void BuildZip_roundtrips_entries()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["main.py"] = Encoding.UTF8.GetBytes("AGENT_VERSION = \"1\"\n"),
            ["static/kiosk.html"] = Encoding.UTF8.GetBytes("<html>"),
        };
        using var zip = new ZipArchive(new MemoryStream(AgentUpdater.BuildZip(files)));
        Assert.Equal(2, zip.Entries.Count);
        using var r = new StreamReader(zip.GetEntry("static/kiosk.html")!.Open());
        Assert.Equal("<html>", r.ReadToEnd());
    }

    [Fact]
    public void Embedded_bundle_contains_complete_agent_and_no_other_root_files()
    {
        var files = PiSignage.Control.AgentBundle.Files();
        var rootFiles = files.Keys
            .Where(path => !path.Contains('/'))
            .OrderBy(path => path)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "control_auth.py",
                "delivery_reset.py",
                "main.py",
                "trust.py",
            },
            rootFiles);
        Assert.Contains(files.Keys, path => path.StartsWith("static/"));
        Assert.All(
            files.Keys,
            path => Assert.True(
                rootFiles.Contains(path) || path.StartsWith("static/"),
                $"Unexpected embedded agent path: {path}"));
    }

    // fake HTTP handler: scripted responses per URL
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<string> Requests = new();
        public List<HttpRequestMessage> RequestMessages = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new(HttpStatusCode.OK);
        public System.Net.Http.Headers.ContentDispositionHeaderValue? CapturedContentDisposition;
        public byte[]? CapturedFileBytes;
        public HttpRequestMessage? UpdateRequest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Requests.Add($"{req.Method} {req.RequestUri!.PathAndQuery}");
            RequestMessages.Add(req);
            // read the multipart content now, before responding — the caller disposes it later
            if (req.Content is MultipartFormDataContent multipart)
            {
                UpdateRequest = req;
                var part = multipart.First();
                CapturedContentDisposition = part.Headers.ContentDisposition;
                CapturedFileBytes = await part.ReadAsByteArrayAsync(ct);
            }
            return Respond(req);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task PushAsync_posts_then_polls_until_version_matches()
    {
        var handler = new FakeHandler();
        int statusCalls = 0;
        handler.Respond = req =>
            req.RequestUri!.AbsolutePath == "/api/update"
                ? Json("{\"ok\": true, \"version\": \"2\"}")
                : (++statusCalls < 3
                    ? Json("{\"agent_version\": \"1\"}")     // still old / restarting
                    : Json("{\"agent_version\": \"2\"}"));
        using var http = new HttpClient(handler);
        var zipBytes = new byte[] { 1, 2, 3 };
        await AgentUpdater.PushAsync(http, "http://pi:8080/", zipBytes, "2", Context(),
            timeout: TimeSpan.FromSeconds(30), pollDelay: TimeSpan.Zero);
        Assert.Equal("POST /api/update", handler.Requests[0]);
        Assert.All(
            handler.Requests.Skip(1),
            request => Assert.Equal("GET /api/status", request));
        Assert.True(statusCalls >= 3);
        Assert.Equal("file", handler.CapturedContentDisposition?.Name?.Trim('"'));
        Assert.Equal(zipBytes, handler.CapturedFileBytes);
        Assert.Equal(
            "test-controller",
            Header(handler.UpdateRequest!, "X-PiSignage-Controller"));
        Assert.True(long.Parse(
            Header(handler.UpdateRequest!, "X-PiSignage-Counter")) > 0);
        Assert.Equal(
            Sha256Hex(zipBytes),
            Header(handler.UpdateRequest!, "X-PiSignage-Entity-SHA256"));
        Assert.Equal(
            64,
            Header(handler.UpdateRequest!, "X-PiSignage-Signature").Length);
        Assert.All(
            handler.RequestMessages.Where(request => request.Method == HttpMethod.Get),
            AssertNoControlHeaders);
    }

    [Fact]
    public async Task PushAsync_throws_on_404_old_agent()
    {
        var handler = new FakeHandler { Respond = _ => new(HttpStatusCode.NotFound) };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            AgentUpdater.PushAsync(
                http,
                "http://pi:8080",
                new byte[] { 1 },
                "2",
                Context()));
    }

    [Fact]
    public async Task PushAsync_times_out_when_pi_never_comes_back()
    {
        var handler = new FakeHandler();
        handler.Respond = req =>
            req.RequestUri!.AbsolutePath == "/api/update"
                ? Json("{\"ok\": true, \"version\": \"2\"}")
                : Json("{\"agent_version\": \"1\"}");  // never updates
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            AgentUpdater.PushAsync(
                http,
                "http://pi:8080",
                new byte[] { 1 },
                "2",
                Context(),
                timeout: TimeSpan.FromMilliseconds(50), pollDelay: TimeSpan.Zero));
    }

    static string Header(HttpRequestMessage request, string name) =>
        request.Headers.GetValues(name).Single();

    static void AssertNoControlHeaders(HttpRequestMessage request)
    {
        Assert.DoesNotContain(
            request.Headers,
            header => header.Key.StartsWith(
                "X-PiSignage-",
                StringComparison.OrdinalIgnoreCase));
    }

    static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    static ControlContext Context()
    {
        long counter = 0;
        return new ControlContext(
            "device-id",
            "test-controller",
            Enumerable.Repeat((byte)1, 32).ToArray(),
            () => Interlocked.Increment(ref counter));
    }
}

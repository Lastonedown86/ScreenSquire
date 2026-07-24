using System.IO.Compression;
using System.Net;
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

    // fake HTTP handler: scripted responses per URL
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<string> Requests = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new(HttpStatusCode.OK);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Requests.Add($"{req.Method} {req.RequestUri!.PathAndQuery}");
            return Task.FromResult(Respond(req));
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
        await AgentUpdater.PushAsync(http, "http://pi:8080", new byte[] { 1 }, "2",
            timeout: TimeSpan.FromSeconds(30), pollDelay: TimeSpan.Zero);
        Assert.Equal("POST /api/update", handler.Requests[0]);
        Assert.True(statusCalls >= 3);
    }

    [Fact]
    public async Task PushAsync_throws_on_404_old_agent()
    {
        var handler = new FakeHandler { Respond = _ => new(HttpStatusCode.NotFound) };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            AgentUpdater.PushAsync(http, "http://pi:8080", new byte[] { 1 }, "2"));
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
            AgentUpdater.PushAsync(http, "http://pi:8080", new byte[] { 1 }, "2",
                timeout: TimeSpan.FromMilliseconds(50), pollDelay: TimeSpan.Zero));
    }
}

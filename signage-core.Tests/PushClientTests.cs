using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using PiSignage.Signage;
using Xunit;

public class PushClientTests
{
    // 1x1 PNG
    static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public byte[]? LastBody { get; private set; }
        public byte[]? LastUploadedEntity { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; init; } =
            _ => Json("""{"ok":true}""");

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Last = request;
            LastBody = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            if (request.Content is MultipartFormDataContent multipart)
                LastUploadedEntity = await multipart.First()
                    .ReadAsByteArrayAsync(cancellationToken);
            return Respond(request);
        }
    }

    [Fact]
    public async Task UploadMedia_signs_source_bytes_and_escaped_destination()
    {
        var handler = new CapturingHandler
        {
            Respond = _ => Json(
                """{"ok":true,"name":"pairings test.png","type":"image","bytes":68}"""),
        };
        using var http = new HttpClient(handler);
        var client = new PushClient(http);

        var path = await client.UploadMediaAsync(
            "http://pi:8080",
            "pairings test.png",
            Png,
            Context());

        Assert.Equal("/media/pairings test.png", path);
        Assert.Equal(
            "http://pi:8080/api/media?name=pairings%20test.png",
            handler.Last!.RequestUri!.AbsoluteUri);
        AssertSigned(handler.Last, Png);
        Assert.Equal(Png, handler.LastUploadedEntity);
    }

    [Fact]
    public async Task PostDashboard_signs_the_exact_transmitted_json()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var client = new PushClient(http);
        var state = new DashboardState();
        state.Boards["pairings"] = "/media/pairings.png";

        await client.PostDashboardAsync(
            "http://pi:8080",
            DashboardPayload.Build(state, new RoundTimer()),
            Context());

        Assert.Equal("POST", handler.Last!.Method.Method);
        Assert.Equal("/api/dashboard", handler.Last.RequestUri!.AbsolutePath);
        AssertSigned(handler.Last, handler.LastBody!);
        Assert.Contains(
            "\"pairings\":\"/media/pairings.png\"",
            Encoding.UTF8.GetString(handler.LastBody!));
    }

    [Fact]
    public async Task Dashboard_and_media_reads_remain_unsigned()
    {
        var dashboardHandler = new CapturingHandler
        {
            Respond = _ => Json(
                """{"view_data":{"boards":{}},"timer":{"state":"stopped"}}"""),
        };
        var dashboardClient = new PushClient(new HttpClient(dashboardHandler));

        await dashboardClient.GetDashboardAsync("http://pi:8080");

        AssertNoControlHeaders(dashboardHandler.Last!);

        var mediaHandler = new CapturingHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Png),
            },
        };
        var mediaClient = new PushClient(new HttpClient(mediaHandler));

        Assert.Equal(
            Png,
            await mediaClient.GetMediaAsync(
                "http://pi:8080",
                "/media/pairings.png"));
        AssertNoControlHeaders(mediaHandler.Last!);
    }

    static void AssertSigned(HttpRequestMessage request, byte[] expectedEntity)
    {
        Assert.Equal("test-controller", Header(request, "X-PiSignage-Controller"));
        Assert.True(long.Parse(Header(request, "X-PiSignage-Counter")) > 0);
        Assert.Equal(
            Sha256Hex(expectedEntity),
            Header(request, "X-PiSignage-Entity-SHA256"));
        Assert.Equal(64, Header(request, "X-PiSignage-Signature").Length);
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

    static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

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

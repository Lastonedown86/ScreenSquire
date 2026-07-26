using System.Net;
using System.Security.Cryptography;
using System.Text;
using PiSignage.Control;
using PiSignage.Signage;

namespace signage_core.Tests;

public class ApiClientTests
{
    [Fact]
    public async Task Reads_remain_unsigned()
    {
        var handler = new RecordingHandler();
        using var client = new ApiClient("pi", 8080, handler);

        await client.GetStatusAsync();
        await client.GetPlaylistAsync();
        await client.GetMediaAsync();
        await client.GetKioskAsync();

        Assert.All(
            handler.Requests,
            request => Assert.DoesNotContain(
                request.Headers,
                header => header.Key.StartsWith(
                    "X-PiSignage-",
                    StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Legacy_unsigned_context_sends_mutations_without_auth_headers()
    {
        var handler = new RecordingHandler();
        using var client = new ApiClient("pi", 8080, handler);
        var context = ControlContext.LegacyUnsigned();

        await client.NextAsync(context);
        await client.SetNameAsync("Front TV", context);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(
            handler.Requests,
            request => Assert.DoesNotContain(
                request.Headers,
                header => header.Key.StartsWith(
                    "X-PiSignage-",
                    StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Every_mutation_family_signs_the_exact_transmitted_entity()
    {
        var handler = new RecordingHandler();
        using var client = new ApiClient("pi", 8080, handler);
        var context = Context();
        var mediaBytes = new byte[] { 1, 2, 3, 4, 5 };
        var mediaPath = Path.Combine(
            Path.GetTempPath(),
            $"signed media {Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(mediaPath, mediaBytes);

        try
        {
            await client.PutPlaylistAsync(
                new Playlist
                {
                    Items =
                    {
                        new PlaylistItem
                        {
                            Type = "url",
                            Source = "https://example.test",
                            Duration = 10,
                        },
                    },
                },
                context);
            await client.UploadMediaAsync(mediaPath, context);
            await client.DeleteMediaAsync("old image.png", context);
            await client.DetachMediaAsync("old image.png", context);
            await client.RenameMediaAsync("old image.png", "new image", context);
            await client.ShowNowAsync(
                new ShowNowRequest
                {
                    Type = "url",
                    Source = "https://example.test/now",
                    Duration = 30,
                },
                context);
            await client.ClearShowNowAsync(context);
            await client.NextAsync(context);
            await client.SetNameAsync("Front TV", context);
            await client.SetKioskAsync(running: false, context);
        }
        finally
        {
            File.Delete(mediaPath);
        }

        Assert.Collection(
            handler.Requests,
            request => AssertJsonMutation(
                request,
                HttpMethod.Put,
                "/api/playlist"),
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(
                    "/api/media?name=" +
                    Uri.EscapeDataString(Path.GetFileName(mediaPath)),
                    request.RequestUri!.PathAndQuery);
                AssertSigned(request, mediaBytes);
                Assert.Equal(mediaBytes, handler.UploadedEntities[request]);
            },
            request => AssertEmptyMutation(
                request,
                HttpMethod.Delete,
                "/api/media/old%20image.png"),
            request => AssertEmptyMutation(
                request,
                HttpMethod.Post,
                "/api/media/old%20image.png/detach"),
            request => AssertJsonMutation(
                request,
                HttpMethod.Post,
                "/api/media/old%20image.png/rename"),
            request => AssertJsonMutation(
                request,
                HttpMethod.Post,
                "/api/show-now"),
            request => AssertEmptyMutation(
                request,
                HttpMethod.Delete,
                "/api/show-now"),
            request => AssertEmptyMutation(
                request,
                HttpMethod.Post,
                "/api/next"),
            request => AssertJsonMutation(
                request,
                HttpMethod.Post,
                "/api/name"),
            request => AssertJsonMutation(
                request,
                HttpMethod.Post,
                "/api/kiosk"));

        Assert.Contains(
            "\"new_name\":\"new image\"",
            Body(handler.Requests[4]));
        Assert.Contains("\"name\":\"Front TV\"", Body(handler.Requests[8]));
        Assert.Contains("\"running\":false", Body(handler.Requests[9]));

        void AssertJsonMutation(
            HttpRequestMessage request,
            HttpMethod method,
            string pathAndQuery)
        {
            Assert.Equal(method, request.Method);
            Assert.Equal(pathAndQuery, request.RequestUri!.PathAndQuery);
            Assert.Equal(
                "application/json",
                request.Content!.Headers.ContentType!.MediaType);
            AssertSigned(request, handler.Bodies[request]);
        }

        void AssertEmptyMutation(
            HttpRequestMessage request,
            HttpMethod method,
            string pathAndQuery)
        {
            Assert.Equal(method, request.Method);
            Assert.Equal(pathAndQuery, request.RequestUri!.PathAndQuery);
            AssertSigned(request, Array.Empty<byte>());
        }

        string Body(HttpRequestMessage request) =>
            Encoding.UTF8.GetString(handler.Bodies[request]);
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

    sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public Dictionary<HttpRequestMessage, byte[]> Bodies { get; } = new();
        public Dictionary<HttpRequestMessage, byte[]> UploadedEntities { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies[request] = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            if (request.Content is MultipartFormDataContent multipart)
            {
                UploadedEntities[request] = await multipart.First()
                    .ReadAsByteArrayAsync(cancellationToken);
            }

            var body = request.RequestUri!.AbsolutePath switch
            {
                "/api/status" =>
                    """{"name":"Front","device_id":"device-id","paired":true}""",
                "/api/playlist" =>
                    """{"items":[],"enabled":true}""",
                "/api/media" =>
                    request.Method == HttpMethod.Get
                        ? """{"files":[]}"""
                        : """{"name":"signed media.png","type":"image","bytes":5}""",
                "/api/kiosk" when request.Method == HttpMethod.Get =>
                    """{"running":true}""",
                var path when path.EndsWith("/rename", StringComparison.Ordinal) =>
                    """{"name":"new image.png"}""",
                "/api/kiosk" =>
                    """{"ok":true,"running":false,"error":null}""",
                _ => """{"ok":true}""",
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}

using System.Net;
using System.Text;
using PiSignage.Signage;
using Xunit;

public class PairingClientTests
{
    const string ControllerId = "11111111111111111111111111111111";

    [Fact]
    public async Task Pair_posts_pin_and_returns_device_secret()
    {
        var handler = new StubHandler(request =>
        {
            return """
                {"device_id":"device-id","controller_id":"11111111111111111111111111111111","controller_secret":"AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE="}
                """;
        });
        var client = new PairingClient(new HttpClient(handler));

        var result = await client.PairAsync(
            "http://10.55.0.1:8080", "12345678", ControllerId);

        Assert.Equal("device-id", result.DeviceId);
        Assert.Equal(ControllerId, result.ControllerId);
        Assert.Equal(32, result.Secret.Length);
        Assert.All(result.Secret, value => Assert.Equal((byte)1, value));
        Assert.Equal(HttpMethod.Post, handler.Last!.Method);
        Assert.Equal("http://10.55.0.1:8080/api/pair", handler.Last.RequestUri!.AbsoluteUri);
        Assert.Equal(
            """{"recovery_pin":"12345678","controller_id":"11111111111111111111111111111111"}""",
            handler.LastBody);
    }

    [Fact]
    public async Task Status_returns_existing_controller_without_exposing_a_secret()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("http://10.55.0.1:8080/api/pair/status", request.RequestUri!.AbsoluteUri);
            return """
                {"device_id":"device-id","paired":true,"controller_id":"other-controller-id"}
                """;
        });
        var client = new PairingClient(new HttpClient(handler));

        var result = await client.GetStatusAsync("http://10.55.0.1:8080");

        Assert.Equal("device-id", result.DeviceId);
        Assert.True(result.Paired);
        Assert.Equal("other-controller-id", result.ControllerId);
    }

    [Theory]
    [InlineData("""{"device_id":"device-id","controller_id":null}""")]
    [InlineData("""{"device_id":"device-id","paired":true,"controller_id":null}""")]
    [InlineData("""{"device_id":"device-id","paired":false,"controller_id":"controller-id"}""")]
    public async Task Status_rejects_missing_or_contradictory_pairing_state(string json)
    {
        var client = new PairingClient(new HttpClient(new StubHandler(_ => json)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.GetStatusAsync("http://10.55.0.1:8080"));
    }

    [Fact]
    public async Task Pair_honors_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new PairingClient(new HttpClient(new CancellationHandler()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.PairAsync(
                "http://10.55.0.1:8080",
                "12345678",
                ControllerId,
                cancellation.Token));
    }

    sealed class StubHandler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Last = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response(request), Encoding.UTF8, "application/json"),
            };
        }
    }

    sealed class CancellationHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }
}

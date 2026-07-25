using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using PiSignage.Signage;
using Xunit;

public class WifiProvisionerTests
{
    sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string? LastBody;
        readonly Func<HttpRequestMessage, (HttpStatusCode, string)> _resp;
        public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> resp) => _resp = resp;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Last = req;
            LastBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            var (code, json) = _resp(req);
            return new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task ConnectPostsCredentialsAndParsesResult()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wifi-vault-{Guid.NewGuid():N}.dat");
        var stub = new StubHandler(_ => (HttpStatusCode.OK,
            "{\"ok\":true,\"connected\":true,\"ip\":\"192.168.1.42\",\"error\":null}"));
        var p = new WifiProvisioner(new HttpClient(stub));
        try
        {
            var vault = new CredentialVault(path, new ReversibleProtector());
            vault.Put("device-id", Enumerable.Repeat((byte)1, 32).ToArray());
            var r = await p.ConnectAsync(
                "http://10.55.0.1:8080",
                "Shop",
                "secret123",
                Context(vault, "device-id"));
            Assert.True(r.Ok); Assert.True(r.Connected); Assert.Equal("192.168.1.42", r.Ip);
            Assert.Contains("\"ssid\":\"Shop\"", stub.LastBody);
            Assert.Contains("\"password\":\"secret123\"", stub.LastBody);
            Assert.EndsWith("/api/wifi", stub.Last!.RequestUri!.AbsolutePath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StatusParses()
    {
        var stub = new StubHandler(_ => (HttpStatusCode.OK,
            "{\"connected\":true,\"ssid\":\"ShopWiFi\",\"ip\":\"192.168.1.42\"}"));
        var p = new WifiProvisioner(new HttpClient(stub));
        var s = await p.GetStatusAsync("http://10.55.0.1:8080");
        Assert.True(s.Connected); Assert.Equal("ShopWiFi", s.Ssid); Assert.Equal("192.168.1.42", s.Ip);
    }

    [Fact]
    public async Task DetectTrueOn200_FalseOnError()
    {
        var ok = new WifiProvisioner(new HttpClient(new StubHandler(_ => (HttpStatusCode.OK, "{}"))));
        Assert.True(await ok.DetectAsync("http://10.55.0.1:8080"));
        var bad = new WifiProvisioner(new HttpClient(new StubHandler(_ => (HttpStatusCode.ServiceUnavailable, ""))));
        Assert.False(await bad.DetectAsync("http://10.55.0.1:8080"));
    }

    [Fact]
    public async Task Connect_signs_the_final_absolute_usb_request_for_the_paired_device()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wifi-vault-{Guid.NewGuid():N}.dat");
        try
        {
            var vault = new CredentialVault(path, new ReversibleProtector());
            vault.Put("device-id", Enumerable.Repeat((byte)1, 32).ToArray());
            var controllerId = vault.Load().ControllerId;
            var stub = new StubHandler(_ => (HttpStatusCode.OK,
                """{"ok":true,"connected":true,"ip":"192.168.1.42","error":null}"""));
            var provisioner = new WifiProvisioner(new HttpClient(stub));

            await provisioner.ConnectAsync(
                "http://10.55.0.1:8080",
                "Shop",
                "secret123",
                Context(vault, "device-id"));

            Assert.Equal("http://10.55.0.1:8080/api/wifi", stub.Last!.RequestUri!.AbsoluteUri);
            Assert.Equal(controllerId, Header(stub.Last, "X-PiSignage-Controller"));
            Assert.Equal("1", Header(stub.Last, "X-PiSignage-Counter"));
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stub.LastBody!)))
                    .ToLowerInvariant(),
                Header(stub.Last, "X-PiSignage-Entity-SHA256"));
            Assert.Equal(64, Header(stub.Last, "X-PiSignage-Signature").Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Detect_honors_cancellation_instead_of_reporting_offline()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provisioner = new WifiProvisioner(
            new HttpClient(new CancellationHandler()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provisioner.DetectAsync(
                "http://10.55.0.1:8080",
                cancellation.Token));
    }

    static string Header(HttpRequestMessage request, string name) =>
        request.Headers.GetValues(name).Single();

    sealed class ReversibleProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Reverse().ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.Reverse().ToArray();
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

    [Fact]
    public async Task Connect_serializes_counter_allocation_through_response_receipt()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"wifi-vault-{Guid.NewGuid():N}.dat");
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var handler = new AsyncHandler(async cancellationToken =>
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                firstEntered.SetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }
            else
            {
                secondEntered.SetResult();
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"ok":true,"connected":true,"ip":"192.168.1.42"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        try
        {
            var vault = new CredentialVault(path, new ReversibleProtector());
            vault.Put("device-id", Enumerable.Repeat((byte)1, 32).ToArray());
            var provisioner = new WifiProvisioner(new HttpClient(handler));
            var first = provisioner.ConnectAsync(
                "http://10.55.0.1:8080",
                "Shop",
                "secret123",
                Context(vault, "device-id"));
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var second = provisioner.ConnectAsync(
                "http://10.55.0.1:8080",
                "Shop",
                "secret123",
                Context(vault, "device-id"));

            var raced = await Task.WhenAny(
                secondEntered.Task,
                Task.Delay(TimeSpan.FromMilliseconds(250)));
            Assert.NotSame(secondEntered.Task, raced);
            Assert.Equal(2, vault.TryGet("device-id")!.NextCounter);

            releaseFirst.SetResult();
            await Task.WhenAll(first, second);
            Assert.Equal(3, vault.TryGet("device-id")!.NextCounter);
        }
        finally
        {
            releaseFirst.TrySetResult();
            File.Delete(path);
        }
    }

    sealed class AsyncHandler(
        Func<CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            respond(cancellationToken);
    }

    static ControlContext Context(CredentialVault vault, string deviceId)
    {
        var credential = vault.TryGet(deviceId)
            ?? throw new InvalidOperationException("Missing test credential.");
        var controllerId = vault.Load().ControllerId;
        return new ControlContext(
            deviceId,
            controllerId,
            credential.Secret,
            () => vault.TakeNextCounter(deviceId));
    }
}

using System.Net;
using System.Net.Http;
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
        var stub = new StubHandler(_ => (HttpStatusCode.OK,
            "{\"ok\":true,\"connected\":true,\"ip\":\"192.168.1.42\",\"error\":null}"));
        var p = new WifiProvisioner(new HttpClient(stub));
        var r = await p.ConnectAsync("http://10.55.0.1:8080", "Shop", "secret123");
        Assert.True(r.Ok); Assert.True(r.Connected); Assert.Equal("192.168.1.42", r.Ip);
        Assert.Contains("\"ssid\":\"Shop\"", stub.LastBody);
        Assert.Contains("\"password\":\"secret123\"", stub.LastBody);
        Assert.EndsWith("/api/wifi", stub.Last!.RequestUri!.AbsolutePath);
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
}

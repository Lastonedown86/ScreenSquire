using System.Net.Http;
using PiSignage.Signage;
using Xunit;

public class PushClientTests
{
    // 1x1 PNG
    static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [Fact]
    public async Task UploadThenPostReachesAgent()
    {
        using var http = new HttpClient();
        try { await http.GetAsync("http://localhost:8080/api/status"); }
        catch { return; }  // agent down -> skip

        var client = new PushClient(http);
        var path = await client.UploadMediaAsync("http://localhost:8080", "pairings-test.png", Png);
        Assert.StartsWith("/media/", path);

        var state = new DashboardState(); state.Boards["pairings"] = path;
        await client.PostDashboardAsync("http://localhost:8080",
            DashboardPayload.Build(state, new RoundTimer()));

        var back = await http.GetStringAsync("http://localhost:8080/api/dashboard");
        Assert.Contains("pairings-test.png", back);
    }
}

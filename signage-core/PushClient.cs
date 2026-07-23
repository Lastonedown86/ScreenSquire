using System.Net.Http;
using System.Net.Http.Json;

namespace PiSignage.Signage;

public sealed class PushClient(HttpClient http)
{
    public async Task<string> UploadMediaAsync(string agentBaseUrl, string filename, byte[] png)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "file", filename);
        var resp = await http.PostAsync(agentBaseUrl.TrimEnd('/') + "/api/media", form);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<UploadResult>()
                   ?? throw new InvalidOperationException("Empty upload response");
        return "/media/" + body.name;
    }

    public async Task PostDashboardAsync(string agentBaseUrl, object payload)
    {
        var resp = await http.PostAsJsonAsync(agentBaseUrl.TrimEnd('/') + "/api/dashboard", payload);
        resp.EnsureSuccessStatusCode();
    }

    // Read the device's current state so the app can restore boards + timer on reopen.
    public async Task<DashboardSnapshot?> GetDashboardAsync(string agentBaseUrl)
        => await http.GetFromJsonAsync<DashboardSnapshot>(agentBaseUrl.TrimEnd('/') + "/api/dashboard");

    // Fetch a board image's bytes (mediaPath is like "/media/pairings-123.png").
    public async Task<byte[]> GetMediaAsync(string agentBaseUrl, string mediaPath)
        => await http.GetByteArrayAsync(agentBaseUrl.TrimEnd('/') + mediaPath);

    private sealed record UploadResult(bool ok, string name, string type, long bytes);
}

// Wire shape of GET /api/dashboard (property names match the JSON keys exactly).
public sealed class DashboardSnapshot
{
    public ViewDataDto view_data { get; set; } = new();
    public TimerDto timer { get; set; } = new();

    public sealed class ViewDataDto
    {
        public Dictionary<string, string> boards { get; set; } = new();
    }

    public sealed class TimerDto
    {
        public string? state { get; set; }
        public long? endsAt { get; set; }     // epoch ms
        public int? remaining { get; set; }
        public int? round { get; set; }
        public string? label { get; set; }
    }
}

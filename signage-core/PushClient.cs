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

    private sealed record UploadResult(bool ok, string name, string type, long bytes);
}

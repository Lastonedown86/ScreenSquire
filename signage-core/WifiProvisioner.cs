using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PiSignage.Signage;

public sealed class WifiResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("connected")] public bool Connected { get; set; }
    [JsonPropertyName("ip")] public string? Ip { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class WifiStatus
{
    [JsonPropertyName("connected")] public bool Connected { get; set; }
    [JsonPropertyName("ssid")] public string? Ssid { get; set; }
    [JsonPropertyName("ip")] public string? Ip { get; set; }
}

public sealed class WifiProvisioner(HttpClient http)
{
    public async Task<bool> DetectAsync(string baseUrl)
    {
        try
        {
            var r = await http.GetAsync(baseUrl.TrimEnd('/') + "/api/status");
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<WifiResult> ConnectAsync(string baseUrl, string ssid, string password)
    {
        var resp = await http.PostAsJsonAsync(baseUrl.TrimEnd('/') + "/api/wifi", new { ssid, password });
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<WifiResult>() ?? new WifiResult();
    }

    public async Task<WifiStatus> GetStatusAsync(string baseUrl)
        => await http.GetFromJsonAsync<WifiStatus>(baseUrl.TrimEnd('/') + "/api/wifi/status") ?? new WifiStatus();
}

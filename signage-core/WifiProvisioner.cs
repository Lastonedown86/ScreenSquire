using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task<bool> DetectAsync(
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var r = await http.GetAsync(
                baseUrl.TrimEnd('/') + "/api/status",
                cancellationToken);
            return r.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch { return false; }
    }

    public async Task<WifiResult> ConnectAsync(
        string baseUrl,
        string ssid,
        string password,
        ControlContext context,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { ssid, password });
        var endpoint = new Uri(baseUrl.TrimEnd('/') + "/api/wifi", UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var resp = await SignedControlRequest.SendAsync(
            http,
            request,
            context,
            body,
            cancellationToken);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<WifiResult>(
            cancellationToken: cancellationToken) ?? new WifiResult();
    }

    public async Task<WifiStatus> GetStatusAsync(
        string baseUrl,
        CancellationToken cancellationToken = default)
        => await http.GetFromJsonAsync<WifiStatus>(
            baseUrl.TrimEnd('/') + "/api/wifi/status",
            cancellationToken) ?? new WifiStatus();
}

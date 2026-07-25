using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
    public async Task<bool> DetectAsync(string baseUrl)
    {
        try
        {
            var r = await http.GetAsync(baseUrl.TrimEnd('/') + "/api/status");
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<WifiResult> ConnectAsync(
        string baseUrl,
        string ssid,
        string password,
        CredentialVault vault,
        string deviceId)
    {
        ArgumentNullException.ThrowIfNull(vault);
        var credential = vault.TryGet(deviceId)
            ?? throw new KeyNotFoundException(
                $"No controller credential exists for device '{deviceId}'.");
        var body = JsonSerializer.SerializeToUtf8Bytes(new { ssid, password });
        var endpoint = new Uri(baseUrl.TrimEnd('/') + "/api/wifi", UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var controllerId = vault.Load().ControllerId;
        var counter = vault.TakeNextCounter(deviceId);
        var entityHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        ControlRequestSigner.Sign(
            request, controllerId, credential.Secret, counter, entityHash);
        using var resp = await http.SendAsync(request);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<WifiResult>() ?? new WifiResult();
    }

    public async Task<WifiStatus> GetStatusAsync(string baseUrl)
        => await http.GetFromJsonAsync<WifiStatus>(baseUrl.TrimEnd('/') + "/api/wifi/status") ?? new WifiStatus();
}

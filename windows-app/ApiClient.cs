using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace PiSignage.Control;

/// <summary>Talks to one Pi's agent API. Create per-device.</summary>
public class ApiClient : IDisposable
{
    private readonly HttpClient _http;
    public string BaseUrl { get; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ApiClient(string host, int port = 8080)
    {
        BaseUrl = $"http://{host.Trim()}:{port}";
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(8) };
    }

    public Task<StatusInfo?> GetStatusAsync() =>
        _http.GetFromJsonAsync<StatusInfo>("/api/status");

    public Task<Playlist?> GetPlaylistAsync() =>
        _http.GetFromJsonAsync<Playlist>("/api/playlist");

    public async Task PutPlaylistAsync(Playlist playlist)
    {
        var resp = await _http.PutAsJsonAsync("/api/playlist", playlist, JsonOpts);
        await ThrowIfError(resp);
    }

    public async Task<List<MediaFile>> GetMediaAsync()
    {
        var r = await _http.GetFromJsonAsync<MediaListResponse>("/api/media");
        return r?.Files ?? new List<MediaFile>();
    }

    public async Task<MediaFile?> UploadMediaAsync(string filePath)
    {
        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(filePath);
        var content = new StreamContent(fs);
        form.Add(content, "file", Path.GetFileName(filePath));
        // uploads of big videos can take a while
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var resp = await _http.PostAsync("/api/media", form, cts.Token);
        await ThrowIfError(resp);
        return await resp.Content.ReadFromJsonAsync<MediaFile>();
    }

    public async Task DeleteMediaAsync(string name)
    {
        var resp = await _http.DeleteAsync($"/api/media/{Uri.EscapeDataString(name)}");
        await ThrowIfError(resp);
    }

    /// <summary>Takes a file off the playlist and any dashboard board so it can be deleted or replaced.</summary>
    public async Task DetachMediaAsync(string name)
    {
        var resp = await _http.PostAsync($"/api/media/{Uri.EscapeDataString(name)}/detach", null);
        await ThrowIfError(resp);
    }

    /// <summary>Renames a media file; the Pi rewrites playlist/dashboard references. Returns the new full filename.</summary>
    public async Task<string> RenameMediaAsync(string name, string newBaseName)
    {
        var resp = await _http.PostAsJsonAsync(
            $"/api/media/{Uri.EscapeDataString(name)}/rename", new { new_name = newBaseName });
        await ThrowIfError(resp);
        var r = await resp.Content.ReadFromJsonAsync<RenameResult>();
        return r?.Name ?? name;
    }

    private class RenameResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")] public string? Name { get; set; }
    }

    public async Task ShowNowAsync(ShowNowRequest req)
    {
        var resp = await _http.PostAsJsonAsync("/api/show-now", req, JsonOpts);
        await ThrowIfError(resp);
    }

    public async Task ClearShowNowAsync()
    {
        var resp = await _http.DeleteAsync("/api/show-now");
        await ThrowIfError(resp);
    }

    public async Task NextAsync()
    {
        var resp = await _http.PostAsync("/api/next", null);
        await ThrowIfError(resp);
    }

    public async Task SetNameAsync(string name)
    {
        var resp = await _http.PostAsJsonAsync("/api/name", new { name });
        await ThrowIfError(resp);
    }

    public Task<KioskState?> GetKioskAsync() => _http.GetFromJsonAsync<KioskState>("/api/kiosk");

    public async Task<bool> SetKioskAsync(bool running)
    {
        var resp = await _http.PostAsJsonAsync("/api/kiosk", new { running });
        await ThrowIfError(resp);
        var r = await resp.Content.ReadFromJsonAsync<KioskResult>();
        return r?.Ok ?? false;
    }

    private static async Task ThrowIfError(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        string detail = "";
        try
        {
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var d))
                detail = d.GetString() ?? "";
        }
        catch { /* body wasn't JSON */ }
        throw new HttpRequestException(
            string.IsNullOrEmpty(detail) ? $"Request failed ({(int)resp.StatusCode})" : detail,
            null, resp.StatusCode);   // callers can react to e.g. 409 Conflict
    }

    public void Dispose() => _http.Dispose();
}

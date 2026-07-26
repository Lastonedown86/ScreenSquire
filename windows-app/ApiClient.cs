using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using PiSignage.Signage;

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

    public ApiClient(string host, int port)
        : this(host, port, new HttpClientHandler())
    {
    }

    public ApiClient(string host, int port, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        BaseUrl = $"http://{host.Trim()}:{port}";
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(8),
        };
    }

    public Task<StatusInfo?> GetStatusAsync() =>
        _http.GetFromJsonAsync<StatusInfo>("/api/status");

    public Task<Playlist?> GetPlaylistAsync() =>
        _http.GetFromJsonAsync<Playlist>("/api/playlist");

    public async Task PutPlaylistAsync(
        Playlist playlist,
        ControlContext context)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(playlist, JsonOpts);
        using var resp = await SendJsonAsync(
            HttpMethod.Put,
            "/api/playlist",
            body,
            context);
        await ThrowIfError(resp);
    }

    public async Task<List<MediaFile>> GetMediaAsync()
    {
        var r = await _http.GetFromJsonAsync<MediaListResponse>("/api/media");
        return r?.Files ?? new List<MediaFile>();
    }

    public async Task<MediaFile?> UploadMediaAsync(
        string filePath,
        ControlContext context)
    {
        // Read once so the signed source bytes are exactly the bytes transmitted.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var source = await File.ReadAllBytesAsync(filePath, cts.Token);
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(source);
        form.Add(content, "file", Path.GetFileName(filePath));
        using var request = Request(
            HttpMethod.Post,
            "/api/media?name=" +
            Uri.EscapeDataString(Path.GetFileName(filePath)));
        request.Content = form;
        using var resp = await SignedControlRequest.SendAsync(
            _http,
            request,
            context,
            source,
            cts.Token);
        await ThrowIfError(resp);
        return await resp.Content.ReadFromJsonAsync<MediaFile>();
    }

    public async Task DeleteMediaAsync(
        string name,
        ControlContext context)
    {
        using var resp = await SendEmptyAsync(
            HttpMethod.Delete,
            $"/api/media/{Uri.EscapeDataString(name)}",
            context);
        await ThrowIfError(resp);
    }

    /// <summary>Takes a file off the playlist and any dashboard board so it can be deleted or replaced.</summary>
    public async Task DetachMediaAsync(
        string name,
        ControlContext context)
    {
        using var resp = await SendEmptyAsync(
            HttpMethod.Post,
            $"/api/media/{Uri.EscapeDataString(name)}/detach",
            context);
        await ThrowIfError(resp);
    }

    /// <summary>Renames a media file; the Pi rewrites playlist/dashboard references. Returns the new full filename.</summary>
    public async Task<string> RenameMediaAsync(
        string name,
        string newBaseName,
        ControlContext context)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new { new_name = newBaseName },
            JsonOpts);
        using var resp = await SendJsonAsync(
            HttpMethod.Post,
            $"/api/media/{Uri.EscapeDataString(name)}/rename",
            body,
            context);
        await ThrowIfError(resp);
        var r = await resp.Content.ReadFromJsonAsync<RenameResult>();
        return r?.Name ?? name;
    }

    private class RenameResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")] public string? Name { get; set; }
    }

    public async Task ShowNowAsync(
        ShowNowRequest req,
        ControlContext context)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(req, JsonOpts);
        using var resp = await SendJsonAsync(
            HttpMethod.Post,
            "/api/show-now",
            body,
            context);
        await ThrowIfError(resp);
    }

    public async Task ClearShowNowAsync(ControlContext context)
    {
        using var resp = await SendEmptyAsync(
            HttpMethod.Delete,
            "/api/show-now",
            context);
        await ThrowIfError(resp);
    }

    public async Task NextAsync(ControlContext context)
    {
        using var resp = await SendEmptyAsync(
            HttpMethod.Post,
            "/api/next",
            context);
        await ThrowIfError(resp);
    }

    public async Task SetNameAsync(
        string name,
        ControlContext context)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { name }, JsonOpts);
        using var resp = await SendJsonAsync(
            HttpMethod.Post,
            "/api/name",
            body,
            context);
        await ThrowIfError(resp);
    }

    public Task<KioskState?> GetKioskAsync() => _http.GetFromJsonAsync<KioskState>("/api/kiosk");

    public async Task<bool> SetKioskAsync(
        bool running,
        ControlContext context)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { running }, JsonOpts);
        using var resp = await SendJsonAsync(
            HttpMethod.Post,
            "/api/kiosk",
            body,
            context);
        await ThrowIfError(resp);
        var r = await resp.Content.ReadFromJsonAsync<KioskResult>();
        return r?.Ok ?? false;
    }

    public async Task<RemoteDesktopSession?> StartRemoteDesktopAsync(ControlContext context)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { running = true }, JsonOpts);
        using var resp = await SendJsonAsync(HttpMethod.Post, "/api/remote-desktop", body, context);
        await ThrowIfError(resp);
        return await resp.Content.ReadFromJsonAsync<RemoteDesktopSession>();
    }

    public async Task StopRemoteDesktopAsync(ControlContext context)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { running = false }, JsonOpts);
        using var resp = await SendJsonAsync(HttpMethod.Post, "/api/remote-desktop", body, context);
        await ThrowIfError(resp);
    }

    async Task<HttpResponseMessage> SendJsonAsync(
        HttpMethod method,
        string pathAndQuery,
        byte[] body,
        ControlContext context,
        CancellationToken cancellationToken = default)
    {
        using var request = Request(method, pathAndQuery);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return await SignedControlRequest.SendAsync(
            _http,
            request,
            context,
            body,
            cancellationToken);
    }

    async Task<HttpResponseMessage> SendEmptyAsync(
        HttpMethod method,
        string pathAndQuery,
        ControlContext context,
        CancellationToken cancellationToken = default)
    {
        using var request = Request(method, pathAndQuery);
        return await SignedControlRequest.SendAsync(
            _http,
            request,
            context,
            Array.Empty<byte>(),
            cancellationToken);
    }

    HttpRequestMessage Request(HttpMethod method, string pathAndQuery) =>
        new(
            method,
            new Uri(BaseUrl.TrimEnd('/') + pathAndQuery, UriKind.Absolute));

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

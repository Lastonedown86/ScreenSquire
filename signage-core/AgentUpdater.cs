using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PiSignage.Signage;

/// <summary>Pushes an agent software bundle (main.py + static) to a Pi over
/// HTTP and waits for the agent to come back with the new version.</summary>
public static class AgentUpdater
{
    public static string? ParseVersion(string mainPySource)
    {
        var m = Regex.Match(mainPySource, "AGENT_VERSION\\s*=\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Does the bundle inside this exe supersede what the Pi is running?
    /// Deliberately an ordering test, not string inequality: a laptop carrying an
    /// older exe would otherwise offer a perfectly current Pi a silent downgrade.
    /// AGENT_VERSION is CalVer (YYYY.MM.DD.N), which System.Version orders
    /// correctly, and zero-padding does not survive parsing either way.</summary>
    public static bool IsNewer(string? bundled, string? installed)
    {
        // Nothing to offer: no bundle, or a version this build cannot interpret.
        if (!Version.TryParse(bundled, out var b)) return false;
        // An agent old enough to predate AGENT_VERSION always needs the update.
        if (!Version.TryParse(installed, out var i)) return true;
        return b > i;
    }

    public static byte[] BuildZip(IReadOnlyDictionary<string, byte[]> files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, bytes) in files)
            {
                using var s = zip.CreateEntry(path).Open();
                s.Write(bytes);
            }
        return ms.ToArray();
    }

    public static async Task PushAsync(HttpClient http, string baseUrl, byte[] zip,
        string expectedVersion, ControlContext context,
        TimeSpan? timeout = null, TimeSpan? pollDelay = null,
        CancellationToken ct = default)
    {
        var normalizedBaseUrl = baseUrl.TrimEnd('/');
        using var form = new MultipartFormDataContent { { new ByteArrayContent(zip), "file", "agent-update.zip" } };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{normalizedBaseUrl}/api/update")
        {
            Content = form,
        };
        using var resp = await SignedControlRequest.SendAsync(
            http,
            request,
            context,
            zip,
            ct);
        resp.EnsureSuccessStatusCode();

        // the agent restarts itself now; poll until it's back on the new version
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        var delay = pollDelay ?? TimeSpan.FromSeconds(1);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(delay, ct);
            try
            {
                using var doc = JsonDocument.Parse(
                    await http.GetStringAsync(
                        $"{normalizedBaseUrl}/api/status",
                        ct));
                if (doc.RootElement.TryGetProperty("agent_version", out var v) &&
                    v.GetString() == expectedVersion)
                    return;
            }
            catch (HttpRequestException) { /* agent still restarting */ }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { /* request timeout mid-restart */ }
        }
        throw new TimeoutException("The Pi didn't come back with the new software version");
    }
}

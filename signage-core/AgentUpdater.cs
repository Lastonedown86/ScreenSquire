using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PiSignage.Signage;

/// <summary>What one sweep of the saved Pis did. The buckets exist because they
/// call for different follow-up: Unreachable and Failed are worth another
/// attempt shortly, Unpaired never is (no amount of retrying invents a
/// credential), and AlreadyCurrent is not news.</summary>
public sealed class FleetResult
{
    public List<string> Updated { get; } = new();
    public List<string> AlreadyCurrent { get; } = new();
    public List<string> Unreachable { get; } = new();
    public List<string> Unpaired { get; } = new();
    public List<(string Name, string Error)> Failed { get; } = new();

    /// <summary>Is there any point trying again soon?</summary>
    // ponytail: an unreachable Pi might well be current already -- we cannot know
    // without reaching it. Retrying is nearly free (one /api/status probe), so
    // treat every unreachable Pi as unfinished rather than track it more finely.
    public bool Settled => Failed.Count == 0 && Unreachable.Count == 0;

    static string Join(List<string> names) =>
        names.Count == 1 ? names[0]
        : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    /// <summary>One plain sentence for a store operator, not a log line.</summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (Updated.Count > 0)
            parts.Add($"{Join(Updated)} updated.");
        if (Failed.Count > 0)
            parts.Add($"{Join(Failed.Select(f => f.Name).ToList())} did not finish updating.");
        if (Unreachable.Count > 0)
            parts.Add($"{Join(Unreachable)} " + (Unreachable.Count == 1 ? "was" : "were") +
                      " switched off, and will update automatically once back on.");
        if (Unpaired.Count > 0)
            parts.Add($"{Join(Unpaired)} " + (Unpaired.Count == 1 ? "is" : "are") +
                      " not set up on this laptop yet.");
        if (parts.Count == 0)
            parts.Add(AlreadyCurrent.Count > 0
                ? "Every TV was already up to date."
                : "There are no TVs to update.");
        return string.Join(" ", parts);
    }
}

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

    static async Task<string?> ProbeVersionAsync(
        HttpClient http, string baseUrl, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(
            await http.GetStringAsync($"{baseUrl.TrimEnd('/')}/api/status", ct));
        return doc.RootElement.TryGetProperty("agent_version", out var v)
            ? v.GetString()
            : null;
    }

    /// <summary>Bring every saved Pi up to the bundle inside this exe. Used by
    /// both the manual button and the nightly sweep so there is one definition
    /// of "which Pis need this and what happened to each".</summary>
    // Sequential on purpose, matching MultiPush: a handful of LAN targets, and
    // each push restarts a TV, so doing them one at a time is also kinder to
    // anyone watching. ponytail: parallelize if a store ever runs enough TVs.
    public static async Task<FleetResult> PushFleetAsync(
        HttpClient http,
        IEnumerable<SavedDevice> devices,
        Func<string, ControlContext?> resolveContext,
        byte[] zip,
        string bundledVersion,
        TimeSpan? timeout = null,
        TimeSpan? pollDelay = null,
        CancellationToken ct = default)
    {
        var result = new FleetResult();
        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.Ip)) continue;   // never discovered
            var name = string.IsNullOrWhiteSpace(device.Name) ? device.Ip : device.Name;
            var baseUrl = $"http://{device.Ip}:{device.Port}";

            // Probe before pushing: a Pi that is off, or already current, must not
            // be counted as a failure -- the nightly sweep decides whether to try
            // again from exactly this distinction.
            string? current;
            try
            {
                current = await ProbeVersionAsync(http, baseUrl, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                result.Unreachable.Add(name);
                continue;
            }

            if (!IsNewer(bundledVersion, current))
            {
                result.AlreadyCurrent.Add(name);
                continue;
            }

            // Resolved here rather than by the caller so an unpaired Pi is a
            // reported outcome instead of a thrown KeyNotFoundException.
            var context = resolveContext(device.DeviceId);
            if (context is null)
            {
                result.Unpaired.Add(name);
                continue;
            }

            try
            {
                await PushAsync(http, baseUrl, zip, bundledVersion, context,
                                timeout, pollDelay, ct);
                result.Updated.Add(name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                result.Failed.Add((name, ex.Message));
            }
        }
        return result;
    }
}

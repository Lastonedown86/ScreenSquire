using System.Security.Cryptography;
using System.Text.Json;

namespace PiSignage.Signage;

/// <summary>A release asset this laptop has downloaded and verified, waiting to
/// be swapped in at the next launch.</summary>
public sealed class PendingUpdate
{
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

/// <summary>Where a release lives, once its shape has been checked.</summary>
public sealed record ReleaseInfo(Version Version, string ExeUrl, string SumsUrl);

/// <summary>Self-update for the Windows control app.
///
/// The store never touches this: the app checks GitHub in the background,
/// verifies what it downloads, stages it, and swaps it in at the next launch.
/// Display Pis are deliberately not part of it — they only ever accept software
/// from their paired Controller laptop over the signed channel.</summary>
public static class AppUpdate
{
    public const string ExeName = "PiSignageControl.exe";
    public const string SumsName = "SHA256SUMS.txt";
    public const string PendingName = "pending.json";
    /// <summary>A build with no version injected by the release workflow. Someone
    /// is running this from a developer machine; leave it entirely alone.</summary>
    public static readonly Version DevBuild = new(0, 0, 0);

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>Pull the release shape out of the GitHub API response, rejecting
    /// anything that is not two assets under the exact expected names, served
    /// over TLS by GitHub. This is not a signature — see SECURITY.md — but it
    /// keeps a mistyped or redirected asset from being executed.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("tag_name", out var tag)) return null;
            var raw = tag.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!Version.TryParse(raw.TrimStart('v', 'V'), out var version)) return null;

            if (!root.TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Array) return null;

            string? exe = null, sums = null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var n) ||
                    !asset.TryGetProperty("browser_download_url", out var u)) continue;
                var url = u.GetString();
                if (!IsTrustedAssetUrl(url)) continue;
                switch (n.GetString())
                {
                    case ExeName: exe = url; break;
                    case SumsName: sums = url; break;
                }
            }
            return exe is null || sums is null ? null : new ReleaseInfo(version, exe, sums);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>HTTPS, and a GitHub host. A release asset that claims to live
    /// anywhere else is not one.</summary>
    public static bool IsTrustedAssetUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Is this release worth the download?</summary>
    public static bool ShouldDownload(Version current, Version candidate, Version? staged)
    {
        if (current == DevBuild) return false;         // never touch a local build
        if (candidate <= current) return false;        // current, or a downgrade
        if (staged is not null && staged >= candidate) return false;   // already have it
        return true;
    }

    /// <summary>Find one file's hash in a SHA256SUMS.txt ("<hash>  <name>").</summary>
    public static string? ParseSha256(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 &&
                parts[1].Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                parts[0].Length == 64)
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string PendingPath(string stageDir) => Path.Combine(stageDir, PendingName);
    public static string StagedExePath(string stageDir) => Path.Combine(stageDir, ExeName);

    public static PendingUpdate? LoadPending(string stageDir)
    {
        try
        {
            var path = PendingPath(stageDir);
            if (!File.Exists(path)) return null;
            var pending = JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(path));
            if (pending is null || !Version.TryParse(pending.Version, out _) ||
                pending.Sha256.Length != 64)
            {
                DiscardPending(stageDir);   // same posture as SettingsStore: corrupt -> gone
                return null;
            }
            return pending;
        }
        catch { DiscardPending(stageDir); return null; }
    }

    public static void DiscardPending(string stageDir)
    {
        TryDelete(PendingPath(stageDir));
        TryDelete(StagedExePath(stageDir));
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* next pass */ }
    }

    /// <summary>Download and verify a newer release into the staging directory.
    /// Returns what was staged, or null if there was nothing to do or anything at
    /// all went wrong — a failed check is silent and simply retried later.
    ///
    /// verifyLaunch is handed the downloaded file and must confirm it actually
    /// starts. That is the check that catches a truncated download, an
    /// antivirus quarantine, or a build that cannot run on this machine, and it
    /// happens before the current exe is touched.</summary>
    public static async Task<PendingUpdate?> StageAsync(
        HttpClient http,
        string releaseApiUrl,
        Version current,
        string stageDir,
        Func<string, Task<bool>> verifyLaunch,
        CancellationToken ct = default)
    {
        var release = ParseRelease(await http.GetStringAsync(releaseApiUrl, ct));
        if (release is null) return null;

        var staged = LoadPending(stageDir);
        var stagedVersion = staged is null ? null : Version.Parse(staged.Version);
        if (!ShouldDownload(current, release.Version, stagedVersion)) return null;

        var expected = ParseSha256(await http.GetStringAsync(release.SumsUrl, ct), ExeName);
        if (expected is null) return null;

        Directory.CreateDirectory(stageDir);
        // Kept as .exe: the launch check below has to actually run this file, and
        // Windows is far happier executing something named like a program.
        var temp = Path.Combine(stageDir, "download.exe");
        try
        {
            using (var response = await http.GetAsync(
                       release.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                using var file = File.Create(temp);
                await response.Content.CopyToAsync(file, ct);
            }

            if (Sha256File(temp) != expected) return null;
            if (!await verifyLaunch(temp)) return null;

            DiscardPending(stageDir);
            File.Move(temp, StagedExePath(stageDir), overwrite: true);
            var pending = new PendingUpdate
            {
                Version = release.Version.ToString(),
                Sha256 = expected,
            };
            File.WriteAllText(PendingPath(stageDir), JsonSerializer.Serialize(pending, Opts));
            return pending;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }        // leave no trace; try again next cycle
        finally { TryDelete(temp); }
    }
}

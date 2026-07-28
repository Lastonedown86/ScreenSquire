using System.Net;
using System.Security.Cryptography;
using System.Text;
using PiSignage.Signage;

namespace signage_core.Tests;

public class AppUpdateTests : IDisposable
{
    readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"appupd-{Guid.NewGuid():N}");

    public AppUpdateTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    static string ReleaseJson(
        string tag = "v2026.08.01.1",
        string exeUrl = "https://github.com/o/r/releases/download/v2026.08.01.1/PiSignageControl.exe",
        string sumsUrl = "https://github.com/o/r/releases/download/v2026.08.01.1/SHA256SUMS.txt",
        string exeName = "PiSignageControl.exe") =>
        $$"""
        {"tag_name":"{{tag}}","assets":[
          {"name":"{{exeName}}","browser_download_url":"{{exeUrl}}"},
          {"name":"vncviewer.exe","browser_download_url":"https://github.com/o/r/v/vncviewer.exe"},
          {"name":"SHA256SUMS.txt","browser_download_url":"{{sumsUrl}}"}]}
        """;

    // ---------- display version ----------

    [Fact]
    public void DisplayVersion_prefers_the_padded_tag_over_the_assembly_version()
    {
        // 2026.08.01.1 is what the release is called; the assembly renders 2026.8.1.1.
        Assert.Equal(
            "2026.08.01.1",
            AppUpdate.DisplayVersion("2026.08.01.1", new Version(2026, 8, 1, 1)));
    }

    [Fact]
    public void DisplayVersion_drops_the_commit_sha_the_sdk_appends()
    {
        Assert.Equal(
            "2026.08.01.1",
            AppUpdate.DisplayVersion(
                "2026.08.01.1+1271e66b937ef69fa196e93b7932545b78fcb71e",
                new Version(2026, 8, 1, 1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DisplayVersion_falls_back_to_the_assembly_version(string? informational)
    {
        Assert.Equal(
            "2026.8.1.1",
            AppUpdate.DisplayVersion(informational, new Version(2026, 8, 1, 1)));
    }

    [Fact]
    public void DisplayVersion_marks_an_unstamped_build_as_development()
    {
        // Nobody should report "0.0.0" as though it were a release.
        Assert.Equal(
            "0.0.0 (development build)",
            AppUpdate.DisplayVersion("0.0.0+abc123", AppUpdate.DevBuild));
    }

    // ---------- release parsing ----------

    [Fact]
    public void ParseRelease_extracts_the_version_and_both_assets()
    {
        var r = AppUpdate.ParseRelease(ReleaseJson());
        Assert.NotNull(r);
        Assert.Equal(new Version(2026, 8, 1, 1), r!.Version);
        Assert.EndsWith("PiSignageControl.exe", r.ExeUrl);
        Assert.EndsWith("SHA256SUMS.txt", r.SumsUrl);
    }

    [Theory]
    [InlineData("http://github.com/o/r/PiSignageControl.exe")]        // not TLS
    [InlineData("https://github.evil.example/o/r/PiSignageControl.exe")]
    [InlineData("https://notgithub.com/o/r/PiSignageControl.exe")]
    [InlineData("file:///C:/PiSignageControl.exe")]
    public void ParseRelease_refuses_an_asset_that_is_not_https_on_github(string url)
    {
        Assert.Null(AppUpdate.ParseRelease(ReleaseJson(exeUrl: url)));
    }

    [Fact]
    public void ParseRelease_refuses_a_release_missing_the_expected_asset_name()
    {
        Assert.Null(AppUpdate.ParseRelease(ReleaseJson(exeName: "PiSignageControl-v2.exe")));
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("")]
    public void ParseRelease_refuses_an_unusable_tag(string tag)
    {
        Assert.Null(AppUpdate.ParseRelease(ReleaseJson(tag: tag)));
    }

    [Fact]
    public void ParseRelease_survives_junk()
    {
        Assert.Null(AppUpdate.ParseRelease("not json at all"));
        Assert.Null(AppUpdate.ParseRelease("{}"));
    }

    [Fact]
    public void ParseRelease_accepts_the_padded_tag_the_workflow_produces()
    {
        // The tag is zero-padded to match AGENT_VERSION; the assembly version is
        // not. These must compare equal or every launch would offer an update.
        var r = AppUpdate.ParseRelease(ReleaseJson(tag: "v2026.07.26.1"));
        Assert.Equal(new Version(2026, 7, 26, 1), r!.Version);
    }

    // ---------- what is worth downloading ----------

    [Theory]
    [InlineData("2026.7.26.1", "2026.8.1.1", null, true)]
    [InlineData("2026.8.1.1", "2026.7.26.1", null, false)]   // never downgrade
    [InlineData("2026.8.1.1", "2026.8.1.1", null, false)]    // already current
    [InlineData("0.0.0", "2026.8.1.1", null, false)]         // developer build
    [InlineData("2026.7.26.1", "2026.8.1.1", "2026.8.1.1", false)]  // already staged
    [InlineData("2026.7.26.1", "2026.8.1.1", "2026.7.27.1", true)]  // staged one is stale
    public void ShouldDownload(string current, string candidate, string? staged, bool expected)
    {
        Assert.Equal(expected, AppUpdate.ShouldDownload(
            Version.Parse(current),
            Version.Parse(candidate),
            staged is null ? null : Version.Parse(staged)));
    }

    // ---------- checksums ----------

    [Fact]
    public void ParseSha256_finds_the_right_line()
    {
        var sums =
            "1111111111111111111111111111111111111111111111111111111111111111  vncviewer.exe\n" +
            "2222222222222222222222222222222222222222222222222222222222222222  PiSignageControl.exe\n";
        Assert.Equal(new string('2', 64), AppUpdate.ParseSha256(sums, "PiSignageControl.exe"));
        Assert.Null(AppUpdate.ParseSha256(sums, "missing.exe"));
        Assert.Null(AppUpdate.ParseSha256("garbage", "PiSignageControl.exe"));
    }

    // ---------- pending record ----------

    [Fact]
    public void Pending_round_trips_and_a_corrupt_record_is_discarded()
    {
        File.WriteAllText(AppUpdate.PendingPath(_dir),
            "{\"Version\":\"2026.8.1.1\",\"Sha256\":\"" + new string('a', 64) + "\"}");
        var pending = AppUpdate.LoadPending(_dir);
        Assert.Equal("2026.8.1.1", pending!.Version);

        File.WriteAllText(AppUpdate.PendingPath(_dir), "{ this is not json");
        Assert.Null(AppUpdate.LoadPending(_dir));
        Assert.False(File.Exists(AppUpdate.PendingPath(_dir)));   // cleaned up

        File.WriteAllText(AppUpdate.PendingPath(_dir),
            "{\"Version\":\"nonsense\",\"Sha256\":\"short\"}");
        Assert.Null(AppUpdate.LoadPending(_dir));
    }

    // ---------- staging ----------

    sealed class Handler : HttpMessageHandler
    {
        public required Func<Uri, HttpResponseMessage> Respond;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(Respond(req.RequestUri!));
    }

    static readonly byte[] Payload = Encoding.UTF8.GetBytes("a pretend executable");
    static string PayloadHash =>
        Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();

    HttpClient Server(string? hashOverride = null, byte[]? bodyOverride = null) =>
        new(new Handler
        {
            Respond = uri => uri.AbsolutePath.EndsWith("releases/latest")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson()) }
                : uri.AbsolutePath.EndsWith("SHA256SUMS.txt")
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            $"{hashOverride ?? PayloadHash}  PiSignageControl.exe\n"),
                    }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(bodyOverride ?? Payload),
                    },
        });

    const string Api = "https://api.github.com/repos/o/r/releases/latest";

    [Fact]
    public async Task StageAsync_stores_a_verified_build_and_records_it()
    {
        using var http = Server();
        var pending = await AppUpdate.StageAsync(
            http, Api, new Version(2026, 7, 26, 1), _dir, _ => Task.FromResult(true));

        Assert.Equal("2026.8.1.1", pending!.Version);
        Assert.Equal(PayloadHash, pending.Sha256);
        Assert.Equal(Payload, File.ReadAllBytes(AppUpdate.StagedExePath(_dir)));
        Assert.False(File.Exists(Path.Combine(_dir, "download.exe")));   // temp cleaned up
    }

    [Fact]
    public async Task StageAsync_refuses_a_download_whose_hash_does_not_match()
    {
        using var http = Server(hashOverride: new string('f', 64));
        Assert.Null(await AppUpdate.StageAsync(
            http, Api, new Version(2026, 7, 26, 1), _dir, _ => Task.FromResult(true)));
        Assert.False(File.Exists(AppUpdate.StagedExePath(_dir)));
        Assert.False(File.Exists(Path.Combine(_dir, "download.exe")));
    }

    [Fact]
    public async Task StageAsync_refuses_a_build_that_will_not_start()
    {
        using var http = Server();
        Assert.Null(await AppUpdate.StageAsync(
            http, Api, new Version(2026, 7, 26, 1), _dir, _ => Task.FromResult(false)));
        Assert.False(File.Exists(AppUpdate.StagedExePath(_dir)));
    }

    [Fact]
    public async Task StageAsync_does_nothing_for_a_developer_build()
    {
        using var http = Server();
        Assert.Null(await AppUpdate.StageAsync(
            http, Api, AppUpdate.DevBuild, _dir, _ => Task.FromResult(true)));
        Assert.False(File.Exists(AppUpdate.StagedExePath(_dir)));
    }

    [Fact]
    public async Task StageAsync_does_not_download_the_same_build_twice()
    {
        var downloads = 0;
        using var http = new HttpClient(new Handler
        {
            Respond = uri =>
            {
                if (uri.AbsolutePath.EndsWith("releases/latest"))
                    return new(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson()) };
                if (uri.AbsolutePath.EndsWith("SHA256SUMS.txt"))
                    return new(HttpStatusCode.OK)
                    { Content = new StringContent($"{PayloadHash}  PiSignageControl.exe\n") };
                downloads++;
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
            },
        });

        var current = new Version(2026, 7, 26, 1);
        Assert.NotNull(await AppUpdate.StageAsync(http, Api, current, _dir, _ => Task.FromResult(true)));
        Assert.Null(await AppUpdate.StageAsync(http, Api, current, _dir, _ => Task.FromResult(true)));
        Assert.Equal(1, downloads);
    }

    // ---------- the swap ----------

    (string exe, Version current) Installed(string version = "2026.7.26.1")
    {
        var exe = Path.Combine(_dir, "app", AppUpdate.ExeName);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "the running build");
        return (exe, Version.Parse(version));
    }

    void Stage(byte[] body, string version = "2026.8.1.1", string? hash = null)
    {
        File.WriteAllBytes(AppUpdate.StagedExePath(_dir), body);
        File.WriteAllText(AppUpdate.PendingPath(_dir),
            $"{{\"Version\":\"{version}\",\"Sha256\":\"" +
            (hash ?? Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant()) + "\"}");
    }

    [Fact]
    public void TryApplyPending_swaps_the_exe_and_keeps_the_previous_one()
    {
        var (exe, current) = Installed();
        Stage(Payload);

        Assert.Equal(AppUpdateInstaller.Outcome.Applied,
            AppUpdateInstaller.TryApplyPending(exe, _dir, current));

        Assert.Equal(Payload, File.ReadAllBytes(exe));
        Assert.Equal("the running build",
            File.ReadAllText(AppUpdateInstaller.BackupPath(exe)));
        Assert.False(File.Exists(AppUpdate.PendingPath(_dir)));
        Assert.False(File.Exists(AppUpdate.StagedExePath(_dir)));
    }

    [Fact]
    public void TryApplyPending_does_nothing_without_a_pending_update()
    {
        var (exe, current) = Installed();
        Assert.Equal(AppUpdateInstaller.Outcome.Nothing,
            AppUpdateInstaller.TryApplyPending(exe, _dir, current));
        Assert.Equal("the running build", File.ReadAllText(exe));
    }

    [Fact]
    public void TryApplyPending_discards_a_pending_record_whose_file_is_gone()
    {
        var (exe, current) = Installed();
        Stage(Payload);
        File.Delete(AppUpdate.StagedExePath(_dir));

        Assert.Equal(AppUpdateInstaller.Outcome.Nothing,
            AppUpdateInstaller.TryApplyPending(exe, _dir, current));
        Assert.False(File.Exists(AppUpdate.PendingPath(_dir)));
    }

    [Fact]
    public void TryApplyPending_refuses_a_staged_build_that_has_been_tampered_with()
    {
        var (exe, current) = Installed();
        Stage(Payload, hash: new string('b', 64));

        Assert.Equal(AppUpdateInstaller.Outcome.Nothing,
            AppUpdateInstaller.TryApplyPending(exe, _dir, current));
        Assert.Equal("the running build", File.ReadAllText(exe));
        Assert.False(File.Exists(AppUpdate.StagedExePath(_dir)));
    }

    [Fact]
    public void TryApplyPending_refuses_to_install_over_a_newer_build()
    {
        // Someone hand-copied a newer exe after the download was staged.
        var (exe, _) = Installed();
        Stage(Payload, version: "2026.8.1.1");

        Assert.Equal(AppUpdateInstaller.Outcome.Nothing,
            AppUpdateInstaller.TryApplyPending(exe, _dir, new Version(2026, 9, 1, 1)));
        Assert.Equal("the running build", File.ReadAllText(exe));
        Assert.False(File.Exists(AppUpdate.PendingPath(_dir)));
    }

    [Fact]
    public void TryApplyPending_waits_when_the_previous_version_is_still_locked()
    {
        var (exe, current) = Installed();
        Stage(Payload);
        var backup = AppUpdateInstaller.BackupPath(exe);
        File.WriteAllText(backup, "an older build another instance is running");

        // Hold it open the way a running process would.
        using (var _ = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(AppUpdateInstaller.Outcome.Blocked,
                AppUpdateInstaller.TryApplyPending(exe, _dir, current));
        }

        Assert.Equal("the running build", File.ReadAllText(exe));   // untouched
        Assert.True(File.Exists(AppUpdate.PendingPath(_dir)));      // still pending

        // Once the other instance exits, the next launch goes through.
        Assert.Equal(AppUpdateInstaller.Outcome.Applied,
            AppUpdateInstaller.TryApplyPending(exe, _dir, current));
    }

    [Fact]
    public void CommitPreviousLaunch_removes_the_backup()
    {
        var (exe, _) = Installed();
        File.WriteAllText(AppUpdateInstaller.BackupPath(exe), "old");
        AppUpdateInstaller.CommitPreviousLaunch(exe);
        Assert.False(File.Exists(AppUpdateInstaller.BackupPath(exe)));
    }

    [Fact]
    public void CanSelfUpdate_reports_whether_the_install_directory_is_writable()
    {
        Assert.True(AppUpdateInstaller.CanSelfUpdate(_dir));
        Assert.False(AppUpdateInstaller.CanSelfUpdate(
            Path.Combine(_dir, "does", "not", "exist")));
    }
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSignage.Control;
using PiSignage.Signage;

namespace signage_core.Tests;

public sealed class DeployAgentScriptTests
{
    [Fact]
    public async Task Deployment_uses_compatible_vault_mutex_and_canonical_headers()
    {
        using var fixture = new ScriptFixture();
        var wrapper = fixture.WriteWrapper(
            """
            function global:Invoke-RestMethod {
                param(
                    [Parameter(Position=0)][string]$Uri,
                    [string]$Method = "GET",
                    [hashtable]$Headers,
                    [hashtable]$Form,
                    [int]$TimeoutSec
                )
                if ($Method -ieq "POST") {
                    $zip = [IO.File]::ReadAllBytes($Form.file.FullName)
                    @{
                        Uri = $Uri
                        Headers = $Headers
                        ZipBase64 = [Convert]::ToBase64String($zip)
                    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $env:CAPTURE
                    return [pscustomobject]@{ ok = $true }
                }
                return [pscustomobject]@{ agent_version = $env:EXPECTED_VERSION }
            }
            & $env:DEPLOY_SCRIPT -Hosts 127.0.0.1 -Port 18080
            """);

        using var mutex = new Mutex(false, VaultMutexName(fixture.CredentialsPath));
        Assert.True(mutex.WaitOne());
        var running = fixture.Start(wrapper);
        Thread.Sleep(TimeSpan.FromSeconds(2));

        Assert.False(running.HasExited);
        Assert.False(File.Exists(fixture.CapturePath));
        Assert.Equal(1, fixture.LoadCredential().NextCounter);

        mutex.ReleaseMutex();
        var result = await CompleteAsync(running);

        Assert.Equal(0, result.ExitCode);
        var credential = fixture.LoadCredential();
        Assert.Equal(2, credential.NextCounter);
        Assert.Equal(2, fixture.LoadVault().Revision);

        using var capture = JsonDocument.Parse(File.ReadAllText(fixture.CapturePath));
        var root = capture.RootElement;
        var headers = root.GetProperty("Headers");
        var zip = Convert.FromBase64String(root.GetProperty("ZipBase64").GetString()!);
        var entityHash = Sha256Hex(zip);
        Assert.Equal(
            fixture.ControllerId,
            Header(headers, "X-PiSignage-Controller"));
        Assert.Equal("1", Header(headers, "X-PiSignage-Counter"));
        Assert.Equal(entityHash, Header(headers, "X-PiSignage-Entity-SHA256"));
        var canonical = string.Join(
            "\n",
            fixture.ControllerId,
            "1",
            "POST",
            "/api/update",
            entityHash);
        var expectedSignature = Convert.ToHexString(
            HMACSHA256.HashData(
                fixture.Secret,
                Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        Assert.Equal(
            expectedSignature,
            Header(headers, "X-PiSignage-Signature"));
    }

    [Fact]
    public async Task Failed_send_burns_counter_and_persists_revision()
    {
        using var fixture = new ScriptFixture();
        var wrapper = fixture.WriteWrapper(
            """
            function global:Invoke-RestMethod {
                param(
                    [Parameter(Position=0)][string]$Uri,
                    [string]$Method = "GET",
                    [hashtable]$Headers,
                    [hashtable]$Form,
                    [int]$TimeoutSec
                )
                if ($Method -ieq "POST") { throw "injected send failure" }
                throw "status should not be polled"
            }
            & $env:DEPLOY_SCRIPT -Hosts 127.0.0.1 -Port 18080
            """);

        var result = await CompleteAsync(fixture.Start(wrapper));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("push failed", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, fixture.LoadCredential().NextCounter);
        Assert.Equal(2, fixture.LoadVault().Revision);
    }

    [Fact]
    public async Task WhatIf_lists_paired_target_without_mutation_or_secrets()
    {
        using var fixture = new ScriptFixture();
        var before = File.ReadAllBytes(fixture.CredentialsPath);
        var wrapper = fixture.WriteWrapper(
            """
            function global:Invoke-RestMethod { throw "WhatIf made a network request" }
            & $env:DEPLOY_SCRIPT -Hosts 127.0.0.1 -Port 18080 -WhatIf
            """);

        var result = await CompleteAsync(fixture.Start(wrapper));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Fixture Pi", result.Output);
        Assert.Contains(fixture.DeviceId, result.Output);
        Assert.Contains("paired", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("version", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(fixture.Secret), result.Output);
        Assert.DoesNotContain(Convert.ToHexString(fixture.Secret), result.Output);
        Assert.Equal(before, File.ReadAllBytes(fixture.CredentialsPath));
        Assert.False(File.Exists(fixture.CapturePath));
    }

    [Fact]
    public async Task DeviceId_without_matching_credential_is_refused_before_network()
    {
        using var fixture = new ScriptFixture();
        fixture.SaveDevice("different-device");
        var wrapper = fixture.WriteWrapper(
            """
            function global:Invoke-RestMethod { throw "unpaired target reached network" }
            & $env:DEPLOY_SCRIPT -Hosts 127.0.0.1 -Port 18080
            """);

        var result = await CompleteAsync(fixture.Start(wrapper));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("NOT PAIRED", result.Output);
        Assert.Equal(1, fixture.LoadCredential().NextCounter);
        Assert.Equal(1, fixture.LoadVault().Revision);
        Assert.False(File.Exists(fixture.CapturePath));
    }

    [Fact]
    public async Task Deployment_holds_shared_device_send_lock_through_response()
    {
        using var fixture = new ScriptFixture();
        var entered = Path.Combine(Path.GetDirectoryName(fixture.CapturePath)!, "entered");
        var release = Path.Combine(Path.GetDirectoryName(fixture.CapturePath)!, "release");
        var wrapper = fixture.WriteWrapper(
            """
            function global:Invoke-RestMethod {
                param(
                    [Parameter(Position=0)][string]$Uri,
                    [string]$Method = "GET",
                    [hashtable]$Headers,
                    [hashtable]$Form,
                    [int]$TimeoutSec
                )
                if ($Method -ieq "POST") {
                    [IO.File]::WriteAllText($env:ENTERED, "entered")
                    while (-not [IO.File]::Exists($env:RELEASE)) {
                        Start-Sleep -Milliseconds 20
                    }
                    return [pscustomobject]@{ ok = $true }
                }
                return [pscustomobject]@{ agent_version = $env:EXPECTED_VERSION }
            }
            & $env:DEPLOY_SCRIPT -Hosts 127.0.0.1 -Port 18080
            """);

        var running = fixture.Start(
            wrapper,
            new Dictionary<string, string>
            {
                ["ENTERED"] = entered,
                ["RELEASE"] = release,
            });
        await WaitForFileAsync(entered);
        var lockPath = ControlSendLock.PathFor(
            fixture.CredentialsPath,
            fixture.DeviceId);
        Assert.Throws<IOException>(() => new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None));
        Assert.DoesNotContain(fixture.DeviceId, Path.GetFileName(lockPath));

        File.WriteAllText(release, "release");
        var result = await CompleteAsync(running);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(File.ReadAllBytes(lockPath));
    }

    static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(path))
            await Task.Delay(20, timeout.Token);
    }

    static async Task<ProcessResult> CompleteAsync(Process process)
    {
        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            return new ProcessResult(
                process.ExitCode,
                (await stdout) + Environment.NewLine + (await stderr));
        }
    }

    static string Header(JsonElement headers, string name) =>
        headers.GetProperty(name).GetString()!;

    static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    static string VaultMutexName(string path)
    {
        var normalizedPath = Path.GetFullPath(path).ToUpperInvariant();
        var currentUser = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var hash = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes($"{currentUser}\n{normalizedPath}")));
        return $@"Local\PiSignage.CredentialVault.{hash}";
    }

    sealed record ProcessResult(int ExitCode, string Output);

    sealed class ScriptFixture : IDisposable
    {
        readonly string _root;
        readonly CredentialVault _vault;
        int _wrapperNumber;

        public ScriptFixture()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                $"pisignage-deploy-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            var piDir = Path.Combine(_root, "PiSignage");
            Directory.CreateDirectory(piDir);
            CredentialsPath = Path.Combine(piDir, "credentials.dat");
            CapturePath = Path.Combine(_root, "capture.json");
            DeviceId = "device-fixture";
            Secret = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
            _vault = new CredentialVault(CredentialsPath, new DpapiSecretProtector());
            _vault.Put(DeviceId, Secret);
            ControllerId = _vault.Load().ControllerId;
            DevicesPath = Path.Combine(piDir, "devices.json");
            SaveDevice(DeviceId);
        }

        public string CredentialsPath { get; }
        public string DevicesPath { get; }
        public string CapturePath { get; }
        public string DeviceId { get; }
        public string ControllerId { get; }
        public byte[] Secret { get; }

        public void SaveDevice(string deviceId) =>
            new DeviceStore(DevicesPath).Save(
                new[]
                {
                    new SavedDevice
                    {
                        DeviceId = deviceId,
                        Name = "Fixture Pi",
                        Hostname = "fixture",
                        Ip = "127.0.0.1",
                        Port = 18080,
                    },
                });

        public CredentialVaultData LoadVault() => _vault.Load();

        public ControllerCredential LoadCredential() =>
            _vault.TryGet(DeviceId) ?? throw new InvalidOperationException();

        public string WriteWrapper(string source)
        {
            var path = Path.Combine(_root, $"wrapper-{++_wrapperNumber}.ps1");
            File.WriteAllText(path, source);
            return path;
        }

        public Process Start(
            string wrapper,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            var script = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\deploy-agent.ps1"));
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(wrapper);
            start.Environment["APPDATA"] = _root;
            start.Environment["DEPLOY_SCRIPT"] = script;
            start.Environment["CAPTURE"] = CapturePath;
            start.Environment["EXPECTED_VERSION"] = AgentBundle.Version()!;
            if (environment is not null)
            {
                foreach (var (name, value) in environment)
                    start.Environment[name] = value;
            }
            return Process.Start(start) ?? throw new InvalidOperationException();
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Test temp cleanup is best effort.
            }
        }
    }
}

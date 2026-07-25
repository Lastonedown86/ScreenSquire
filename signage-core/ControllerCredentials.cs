using System.Collections.Concurrent;
using System.Text.Json;

namespace PiSignage.Signage;

public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

public sealed record ControllerCredential(byte[] Secret, long NextCounter);

public sealed class CredentialVaultData
{
    public string ControllerId { get; set; } = Guid.NewGuid().ToString("N");
    public Dictionary<string, ControllerCredential> Devices { get; set; } = new();
}

public sealed class CredentialVault
{
    static readonly ConcurrentDictionary<string, object> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    readonly ISecretProtector _protector;
    readonly object _pathLock;

    public CredentialVault(string path, ISecretProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);

        Path = System.IO.Path.GetFullPath(path);
        _protector = protector;
        _pathLock = PathLocks.GetOrAdd(Path, static _ => new object());
    }

    public CredentialVault(ISecretProtector protector)
        : this(DefaultPath(), protector)
    {
    }

    public string Path { get; }

    public static string DefaultPath()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PiSignage");
        return System.IO.Path.Combine(dir, "credentials.dat");
    }

    public CredentialVaultData Load()
    {
        lock (_pathLock)
        {
            return LoadOrCreate();
        }
    }

    public void Save(CredentialVaultData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_pathLock)
        {
            SaveCore(data);
        }
    }

    public void Put(string deviceId, byte[] secret)
    {
        ValidateDeviceId(deviceId);
        ArgumentNullException.ThrowIfNull(secret);

        lock (_pathLock)
        {
            var data = LoadOrCreate();
            data.Devices[deviceId] = new ControllerCredential(secret.ToArray(), 1);
            SaveCore(data);
        }
    }

    public ControllerCredential? TryGet(string deviceId)
    {
        ValidateDeviceId(deviceId);
        lock (_pathLock)
        {
            var data = LoadOrCreate();
            return data.Devices.TryGetValue(deviceId, out var credential)
                ? Copy(credential)
                : null;
        }
    }

    public void Remove(string deviceId)
    {
        ValidateDeviceId(deviceId);
        lock (_pathLock)
        {
            var data = LoadOrCreate();
            if (data.Devices.Remove(deviceId))
                SaveCore(data);
        }
    }

    public long TakeNextCounter(string deviceId)
    {
        ValidateDeviceId(deviceId);
        lock (_pathLock)
        {
            var data = LoadOrCreate();
            if (!data.Devices.TryGetValue(deviceId, out var credential))
                throw new KeyNotFoundException(
                    $"No controller credential exists for device '{deviceId}'.");

            var allocated = credential.NextCounter;
            var next = checked(allocated + 1);
            data.Devices[deviceId] = credential with { NextCounter = next };
            SaveCore(data);
            return allocated;
        }
    }

    CredentialVaultData LoadOrCreate()
    {
        if (!File.Exists(Path))
        {
            var created = new CredentialVaultData();
            SaveCore(created);
            return created;
        }

        var protectedBytes = File.ReadAllBytes(Path);
        var json = _protector.Unprotect(protectedBytes)
            ?? throw new InvalidDataException("Secret protector returned no plaintext.");
        var data = JsonSerializer.Deserialize<CredentialVaultData>(json, JsonOptions);
        ValidateData(data);
        return data!;
    }

    void SaveCore(CredentialVaultData data)
    {
        ValidateData(data);
        var json = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        var protectedBytes = _protector.Protect(json)
            ?? throw new InvalidDataException("Secret protector returned no ciphertext.");
        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("Credential vault path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = System.IO.Path.Combine(
            directory,
            $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, Path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    static void ValidateData(CredentialVaultData? data)
    {
        if (data is null ||
            string.IsNullOrWhiteSpace(data.ControllerId) ||
            data.Devices is null)
        {
            throw new InvalidDataException("Credential vault data is invalid.");
        }

        foreach (var (deviceId, credential) in data.Devices)
        {
            if (string.IsNullOrWhiteSpace(deviceId) ||
                credential is null ||
                credential.Secret is null ||
                credential.NextCounter < 1)
            {
                throw new InvalidDataException("Credential vault contains an invalid device.");
            }
        }
    }

    static ControllerCredential Copy(ControllerCredential credential) =>
        new(credential.Secret.ToArray(), credential.NextCounter);

    static void ValidateDeviceId(string deviceId) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
}

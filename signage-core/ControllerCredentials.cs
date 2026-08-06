using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
    public long Revision { get; set; }
    public Dictionary<string, ControllerCredential> Devices { get; set; } = new();
}

public sealed class CredentialVault
{
    static readonly ConcurrentDictionary<string, object> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    readonly ISecretProtector _protector;
    readonly object _pathLock;
    readonly string _mutexName;

    public CredentialVault(string path, ISecretProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);

        Path = System.IO.Path.GetFullPath(path);
        _protector = protector;
        _pathLock = PathLocks.GetOrAdd(Path, static _ => new object());
        _mutexName = MutexName(Path);
    }

    public CredentialVault(ISecretProtector protector)
        : this(DefaultPath(), protector)
    {
    }

    public string Path { get; }

    /// <summary>True once an unreadable vault (DPAPI failure after a Windows
    /// profile change, corrupt file) was moved aside and replaced with a fresh
    /// one. The UI uses this to tell the operator to re-pair over USB.</summary>
    public bool RecoveredFromUnreadableVault { get; private set; }

    public static string DefaultPath()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PiSignage");
        return System.IO.Path.Combine(dir, "credentials.dat");
    }

    public CredentialVaultData Load()
    {
        return WithVaultLock(LoadOrCreate);
    }

    public void Save(CredentialVaultData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        WithVaultLock(() =>
        {
            var current = LoadOrCreate();
            if (data.Revision != current.Revision)
            {
                throw new InvalidOperationException(
                    "Credential vault data is stale and cannot be saved.");
            }

            var saved = Copy(data, checked(data.Revision + 1));
            SaveCore(saved);
            data.Revision = saved.Revision;
        });
    }

    public void Put(string deviceId, byte[] secret)
    {
        ValidateDeviceId(deviceId);
        ArgumentNullException.ThrowIfNull(secret);

        WithVaultLock(() =>
        {
            var data = LoadOrCreate();
            data.Devices[deviceId] = new ControllerCredential(secret.ToArray(), 1);
            SaveMutation(data);
        });
    }

    public ControllerCredential? TryGet(string deviceId)
    {
        ValidateDeviceId(deviceId);
        return WithVaultLock(() =>
        {
            var data = LoadOrCreate();
            return data.Devices.TryGetValue(deviceId, out var credential)
                ? Copy(credential)
                : null;
        });
    }

    public void Remove(string deviceId)
    {
        ValidateDeviceId(deviceId);
        WithVaultLock(() =>
        {
            var data = LoadOrCreate();
            if (data.Devices.Remove(deviceId))
                SaveMutation(data);
        });
    }

    public long TakeNextCounter(string deviceId)
    {
        ValidateDeviceId(deviceId);
        return WithVaultLock(() =>
        {
            var data = LoadOrCreate();
            if (!data.Devices.TryGetValue(deviceId, out var credential))
                throw new KeyNotFoundException(
                    $"No controller credential exists for device '{deviceId}'.");

            var allocated = credential.NextCounter;
            var next = checked(allocated + 1);
            data.Devices[deviceId] = credential with { NextCounter = next };
            SaveMutation(data);
            return allocated;
        });
    }

    CredentialVaultData LoadOrCreate()
    {
        if (!File.Exists(Path))
        {
            var created = new CredentialVaultData();
            SaveCore(created);
            return created;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(Path);
            var json = _protector.Unprotect(protectedBytes)
                ?? throw new InvalidDataException("Secret protector returned no plaintext.");
            var data = JsonSerializer.Deserialize<CredentialVaultData>(json, JsonOptions);
            ValidateData(data);
            return data!;
        }
        catch (Exception ex) when (
            ex is CryptographicException or JsonException or InvalidDataException)
        {
            // A vault that can no longer be decrypted (Windows profile reset or
            // migration) or parsed would otherwise fail every operation forever.
            // Quarantine it and start fresh: ownership is re-established with the
            // Recovery PIN over USB, never from this file.
            QuarantineUnreadableVault();
            RecoveredFromUnreadableVault = true;
            var created = new CredentialVaultData();
            SaveCore(created);
            return created;
        }
    }

    void QuarantineUnreadableVault()
    {
        try
        {
            File.Move(Path, Path + ".unreadable", overwrite: true);
        }
        catch (IOException)
        {
            try { File.Delete(Path); } catch (IOException) { }
        }
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
        var committed = false;

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
            committed = true;
        }
        finally
        {
            if (!committed)
                File.Delete(temporaryPath);
        }
    }

    void SaveMutation(CredentialVaultData data)
    {
        data.Revision = checked(data.Revision + 1);
        SaveCore(data);
    }

    T WithVaultLock<T>(Func<T> action)
    {
        lock (_pathLock)
        {
            using var mutex = new Mutex(initiallyOwned: false, _mutexName);
            var ownsMutex = false;
            try
            {
                try
                {
                    mutex.WaitOne();
                    ownsMutex = true;
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }

                return action();
            }
            finally
            {
                if (ownsMutex)
                    mutex.ReleaseMutex();
            }
        }
    }

    void WithVaultLock(Action action) =>
        WithVaultLock(() =>
        {
            action();
            return true;
        });

    static void ValidateData(CredentialVaultData? data)
    {
        if (data is null ||
            string.IsNullOrWhiteSpace(data.ControllerId) ||
            data.Revision < 0 ||
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

    static CredentialVaultData Copy(CredentialVaultData data, long revision) =>
        new()
        {
            ControllerId = data.ControllerId,
            Revision = revision,
            Devices = data.Devices.ToDictionary(
                pair => pair.Key,
                pair => Copy(pair.Value),
                StringComparer.Ordinal),
        };

    static string MutexName(string path)
    {
        var normalizedPath = OperatingSystem.IsWindows()
            ? path.ToUpperInvariant()
            : path;
        var currentUser = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var key = Encoding.UTF8.GetBytes($"{currentUser}\n{normalizedPath}");
        var hash = Convert.ToHexString(SHA256.HashData(key));
        var scope = OperatingSystem.IsWindows() ? @"Local\" : "";
        return $"{scope}PiSignage.CredentialVault.{hash}";
    }

    static void ValidateDeviceId(string deviceId) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
}

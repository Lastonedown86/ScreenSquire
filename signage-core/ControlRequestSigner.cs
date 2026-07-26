using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PiSignage.Signage;

public sealed record ControlContext(
    string DeviceId,
    string ControllerId,
    byte[] Secret,
    Func<long> TakeNextCounter,
    string? CredentialVaultPath = null);

public static class ControlSendLock
{
    public static string PathFor(string credentialVaultPath, string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialVaultPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var directory = Path.GetDirectoryName(Path.GetFullPath(credentialVaultPath))
            ?? throw new InvalidOperationException(
                "Credential vault path has no directory.");
        var deviceHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)));
        return Path.Combine(directory, $".control-send-{deviceHash}.lock");
    }

    public static async Task<FileStream> AcquireAsync(
        string credentialVaultPath,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var path = PathFor(credentialVaultPath, deviceId);
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Send lock has no directory."));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

public static class SignedControlRequest
{
    static readonly ConcurrentDictionary<string, SemaphoreSlim> DeviceLocks =
        new(StringComparer.Ordinal);

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        HttpRequestMessage request,
        ControlContext context,
        byte[] entityBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entityBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.DeviceId);
        ArgumentNullException.ThrowIfNull(context.TakeNextCounter);

        var deviceLock = DeviceLocks.GetOrAdd(
            context.DeviceId,
            static _ => new SemaphoreSlim(1, 1));
        await deviceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = context.CredentialVaultPath is null
                ? null
                : await ControlSendLock.AcquireAsync(
                    context.CredentialVaultPath,
                    context.DeviceId,
                    cancellationToken).ConfigureAwait(false);
            var entityHash = Convert.ToHexString(SHA256.HashData(entityBytes))
                .ToLowerInvariant();
            ControlRequestSigner.Sign(
                request,
                context.ControllerId,
                context.Secret,
                context.TakeNextCounter(),
                entityHash);
            return await http.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            deviceLock.Release();
        }
    }
}

public static class ControlRequestSigner
{
    const string ControllerHeader = "X-PiSignage-Controller";
    const string CounterHeader = "X-PiSignage-Counter";
    const string EntityHashHeader = "X-PiSignage-Entity-SHA256";
    const string SignatureHeader = "X-PiSignage-Signature";

    public static void Sign(
        HttpRequestMessage request,
        string controllerId,
        byte[] secret,
        long counter,
        string entityHash)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateControllerId(controllerId);
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length != 32)
            throw new ArgumentException("Controller secret must contain 32 bytes.", nameof(secret));
        if (counter <= 0)
            throw new ArgumentOutOfRangeException(nameof(counter), "Counter must be positive.");
        ValidateEntityHash(entityHash);

        var requestUri = request.RequestUri
            ?? throw new ArgumentException("The request must have a URI.", nameof(request));
        if (!requestUri.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "The request URI must be absolute before signing.",
                nameof(request));
        }
        var pathAndQuery = requestUri.PathAndQuery;
        var counterText = counter.ToString(CultureInfo.InvariantCulture);
        var canonical = string.Join(
            "\n",
            controllerId,
            counterText,
            request.Method.Method.ToUpperInvariant(),
            pathAndQuery,
            entityHash);
        var signature = Convert.ToHexString(
            HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();

        SetHeader(request, ControllerHeader, controllerId);
        SetHeader(request, CounterHeader, counterText);
        SetHeader(request, EntityHashHeader, entityHash);
        SetHeader(request, SignatureHeader, signature);
    }

    static void SetHeader(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        request.Content?.Headers.Remove(name);
        if (!request.Headers.TryAddWithoutValidation(name, value))
            throw new InvalidOperationException($"Could not set request header '{name}'.");
    }

    static void ValidateControllerId(string controllerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controllerId);
        if (controllerId.Length > 64 || controllerId.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Controller ID must be at most 64 characters and contain no control characters.",
                nameof(controllerId));
        }
    }

    static void ValidateEntityHash(string entityHash)
    {
        ArgumentNullException.ThrowIfNull(entityHash);
        if (entityHash.Length != 64 || entityHash.Any(static c =>
                !((c >= '0' && c <= '9') ||
                  (c >= 'a' && c <= 'f') ||
                  (c >= 'A' && c <= 'F'))))
        {
            throw new ArgumentException(
                "Entity hash must be exactly 64 hexadecimal characters.",
                nameof(entityHash));
        }
    }
}

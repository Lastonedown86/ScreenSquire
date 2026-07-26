using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Text.Json.Serialization;
using PiSignage.Signage;

namespace PiSignage.Control;

public sealed record DeliveryResetResult(bool Ok, string DeviceId);
public sealed record DeliveryCleanupError(string Operation, string Message);
public sealed record DeliveryPreparationOutcome(
    IReadOnlyList<DeliveryCleanupError> CleanupErrors,
    bool ResetWasConfirmedAfterAmbiguousFailure = false);

public interface IDeliveryPreparationOperations
{
    Task<PairStatus> GetPairStatusAsync(
        string baseUrl,
        CancellationToken cancellationToken);

    Task<DeliveryResetResult> SendResetAsync(
        string baseUrl,
        ControlContext context,
        CancellationToken cancellationToken);

    void RemoveCredential(string deviceId);
    void RemoveSavedDevice(string deviceId);
    void RemoveSignageDeviceId(string deviceId);
    void ClearThumbnails(SavedDevice device);
}

/// <summary>
/// Verifies the selected Pi over the fixed USB link, performs its signed reset,
/// and only then removes the builder's local records.
/// </summary>
public sealed class DeliveryPreparation(IDeliveryPreparationOperations operations)
{
    public const string UsbBaseUrl = "http://10.55.0.1:8080";

    readonly IDeliveryPreparationOperations _operations =
        operations ?? throw new ArgumentNullException(nameof(operations));

    public static bool CanPrepare(
        SavedDevice? device,
        Func<string, bool> hasCredential)
    {
        ArgumentNullException.ThrowIfNull(hasCredential);
        if (device is null || string.IsNullOrWhiteSpace(device.DeviceId))
            return false;
        try
        {
            return hasCredential(device.DeviceId);
        }
        catch
        {
            return false;
        }
    }

    public async Task<DeliveryPreparationOutcome> RunAsync(
        SavedDevice device,
        ControlContext controlContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(controlContext);
        if (string.IsNullOrWhiteSpace(device.DeviceId))
        {
            throw new InvalidOperationException(
                "The selected saved device does not have a stable DeviceId.");
        }
        if (!string.Equals(
                device.DeviceId,
                controlContext.DeviceId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The selected device and controller credential do not match.");
        }

        var pairStatus = await _operations.GetPairStatusAsync(
            UsbBaseUrl,
            cancellationToken);
        if (!string.Equals(
                pairStatus.DeviceId,
                device.DeviceId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Pi connected over USB reported a different device identity.");
        }
        if (!pairStatus.Paired ||
            !string.Equals(
                pairStatus.ControllerId,
                controlContext.ControllerId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Pi connected over USB is not paired with this controller.");
        }

        var resetConfirmedAfterAmbiguousFailure = false;
        try
        {
            var reset = await _operations.SendResetAsync(
                UsbBaseUrl,
                controlContext,
                cancellationToken);
            if (!reset.Ok ||
                !string.Equals(
                    reset.DeviceId,
                    device.DeviceId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The Pi returned an invalid delivery-reset result.");
            }
        }
        catch (Exception resetFailure)
            when (!cancellationToken.IsCancellationRequested)
        {
            PairStatus? confirmation = null;
            try
            {
                confirmation = await _operations.GetPairStatusAsync(
                    UsbBaseUrl,
                    cancellationToken);
            }
            catch
            {
                // Preserve the original reset failure. An unreachable or
                // unreadable confirmation endpoint cannot prove the reset.
            }
            if (confirmation is not null &&
                string.Equals(
                    confirmation.DeviceId,
                    device.DeviceId,
                    StringComparison.Ordinal) &&
                !confirmation.Paired)
            {
                resetConfirmedAfterAmbiguousFailure = true;
            }
            else
            {
                ExceptionDispatchInfo.Capture(resetFailure).Throw();
                throw;
            }
        }

        // All builder-side cleanup is intentionally after confirmed remote
        // success. A failed HTTP reset leaves every local recovery record.
        var cleanupErrors = new List<DeliveryCleanupError>();
        AttemptCleanup(
            cleanupErrors,
            "credential",
            () => _operations.RemoveCredential(device.DeviceId));
        AttemptCleanup(
            cleanupErrors,
            "device",
            () => _operations.RemoveSavedDevice(device.DeviceId));
        AttemptCleanup(
            cleanupErrors,
            "settings",
            () => _operations.RemoveSignageDeviceId(device.DeviceId));
        AttemptCleanup(
            cleanupErrors,
            "cache",
            () => _operations.ClearThumbnails(device));
        return new DeliveryPreparationOutcome(
            cleanupErrors.AsReadOnly(),
            resetConfirmedAfterAmbiguousFailure);
    }

    static void AttemptCleanup(
        List<DeliveryCleanupError> errors,
        string operation,
        Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            errors.Add(new DeliveryCleanupError(operation, ex.Message));
        }
    }
}

public sealed class WindowsDeliveryPreparationOperations : IDeliveryPreparationOperations
{
    readonly HttpClient _http;
    readonly PairingClient _pairing;
    readonly CredentialVault _credentialVault;
    readonly DeviceStore _deviceStore;
    readonly SettingsStore _settingsStore;
    readonly AppSettings _settings;
    readonly Action<SavedDevice> _clearThumbnails;

    public WindowsDeliveryPreparationOperations(
        HttpClient http,
        CredentialVault credentialVault,
        DeviceStore deviceStore,
        SettingsStore settingsStore,
        AppSettings settings,
        Action<SavedDevice> clearThumbnails)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _pairing = new PairingClient(_http);
        _credentialVault = credentialVault
            ?? throw new ArgumentNullException(nameof(credentialVault));
        _deviceStore = deviceStore
            ?? throw new ArgumentNullException(nameof(deviceStore));
        _settingsStore = settingsStore
            ?? throw new ArgumentNullException(nameof(settingsStore));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clearThumbnails = clearThumbnails
            ?? throw new ArgumentNullException(nameof(clearThumbnails));
    }

    public Task<PairStatus> GetPairStatusAsync(
        string baseUrl,
        CancellationToken cancellationToken) =>
        _pairing.GetStatusAsync(baseUrl, cancellationToken);

    public async Task<DeliveryResetResult> SendResetAsync(
        string baseUrl,
        ControlContext context,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(
                baseUrl.TrimEnd('/') + "/api/prepare-delivery",
                UriKind.Absolute));
        using var response = await SignedControlRequest.SendAsync(
            _http,
            request,
            context,
            Array.Empty<byte>(),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var wire = await response.Content.ReadFromJsonAsync<DeliveryResetResponse>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidDataException(
                "The Pi returned an empty delivery-reset response.");
        if (string.IsNullOrWhiteSpace(wire.DeviceId))
        {
            throw new InvalidDataException(
                "The Pi returned an invalid delivery-reset identity.");
        }
        return new DeliveryResetResult(wire.Ok, wire.DeviceId);
    }

    public void RemoveCredential(string deviceId) =>
        _credentialVault.Remove(deviceId);

    public void RemoveSavedDevice(string deviceId)
    {
        var devices = _deviceStore.Load();
        devices.RemoveAll(device => string.Equals(
            device.DeviceId,
            deviceId,
            StringComparison.Ordinal));
        _deviceStore.Save(devices);
    }

    public void RemoveSignageDeviceId(string deviceId)
    {
        _settings.SignageDeviceIds.RemoveAll(savedId => string.Equals(
            savedId,
            deviceId,
            StringComparison.Ordinal));
        _settingsStore.Save(_settings);
    }

    public void ClearThumbnails(SavedDevice device) =>
        _clearThumbnails(device);

    sealed class DeliveryResetResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    }
}

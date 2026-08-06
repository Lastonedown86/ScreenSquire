using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PiSignage.Signage;

public sealed record PairResult(string DeviceId, string ControllerId, byte[] Secret);

public sealed record PairStatus(string DeviceId, bool Paired, string? ControllerId);

/// <summary>The Pi refused the pairing for a reason the store operator can fix.
/// The message is written for a person at the setup wizard, not for a log.</summary>
public sealed class PairingRejectedException(string message) : Exception(message);

public sealed class PairingClient(HttpClient http)
{
    public async Task<PairResult> PairAsync(
        string baseUrl,
        string pin,
        string controllerId,
        CancellationToken cancellationToken = default)
    {
        var endpoint = Endpoint(baseUrl, "/api/pair");
        using var response = await http.PostAsJsonAsync(
            endpoint,
            new { recovery_pin = pin, controller_id = controllerId },
            cancellationToken);
        switch ((int)response.StatusCode)
        {
            case 401:
                throw new PairingRejectedException(
                    "That PIN doesn't match this Pi. Check the 8-digit number " +
                    "on the sticker on the bottom of the Pi's case and try again.");
            case 429:
                var wait = (int?)response.Headers.RetryAfter?.Delta?.TotalSeconds is int s && s > 0
                    ? s
                    : 60;
                throw new PairingRejectedException(
                    $"Too many PIN attempts. Wait {wait} seconds, then try again.");
            case 503:
                throw new PairingRejectedException(
                    "This Pi can't accept pairing right now because its identity " +
                    "storage is unavailable. Unplug the Pi's power, plug it back " +
                    "in, and try again — if that doesn't help, contact your installer.");
        }
        response.EnsureSuccessStatusCode();
        var wire = await response.Content.ReadFromJsonAsync<PairResponse>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("The Pi returned an empty pairing response.");
        var secret = Convert.FromBase64String(wire.ControllerSecret);
        if (secret.Length != 32)
            throw new InvalidDataException("The Pi returned an invalid controller secret.");
        if (string.IsNullOrWhiteSpace(wire.DeviceId) ||
            string.IsNullOrWhiteSpace(wire.ControllerId))
        {
            throw new InvalidDataException("The Pi returned an invalid pairing identity.");
        }
        if (!string.Equals(wire.ControllerId, controllerId, StringComparison.Ordinal))
            throw new InvalidDataException("The Pi returned the wrong controller identity.");
        return new PairResult(wire.DeviceId, wire.ControllerId, secret);
    }

    public async Task<PairStatus> GetStatusAsync(
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        var result = await http.GetFromJsonAsync<PairStatusResponse>(
            Endpoint(baseUrl, "/api/pair/status"),
            cancellationToken)
            ?? throw new InvalidDataException("The Pi returned an empty pairing status.");
        if (string.IsNullOrWhiteSpace(result.DeviceId))
            throw new InvalidDataException("The Pi returned an invalid device identity.");
        if (result.Paired is null)
            throw new InvalidDataException("The Pi did not report its pairing state.");
        if (result.Paired.Value && string.IsNullOrWhiteSpace(result.ControllerId))
            throw new InvalidDataException("The paired Pi did not report its controller identity.");
        if (!result.Paired.Value && !string.IsNullOrEmpty(result.ControllerId))
            throw new InvalidDataException("The unpaired Pi reported a controller identity.");
        return new PairStatus(
            result.DeviceId,
            result.Paired.Value,
            result.ControllerId);
    }

    static Uri Endpoint(string baseUrl, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        return new Uri(baseUrl.TrimEnd('/') + path, UriKind.Absolute);
    }

    sealed class PairResponse
    {
        [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
        [JsonPropertyName("controller_id")] public string ControllerId { get; set; } = "";
        [JsonPropertyName("controller_secret")] public string ControllerSecret { get; set; } = "";
    }

    sealed class PairStatusResponse
    {
        [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
        [JsonPropertyName("paired")] public bool? Paired { get; set; }
        [JsonPropertyName("controller_id")] public string? ControllerId { get; set; }
    }
}

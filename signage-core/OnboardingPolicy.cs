namespace PiSignage.Signage;

public static class DeviceIdentityPolicy
{
    public static bool IsMatch(SavedDevice saved, string reportedDeviceId)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (string.IsNullOrWhiteSpace(reportedDeviceId))
            return false;
        return string.IsNullOrWhiteSpace(saved.DeviceId) ||
            string.Equals(saved.DeviceId, reportedDeviceId, StringComparison.Ordinal);
    }

    public static void ApplyVerifiedEndpoint(
        SavedDevice saved,
        string reportedDeviceId,
        string reportedHostname,
        string ip,
        int port)
    {
        if (!IsMatch(saved, reportedDeviceId))
            throw new InvalidDataException("The endpoint reported a different device identity.");
        if (port <= 0)
            throw new ArgumentOutOfRangeException(nameof(port));

        if (string.IsNullOrWhiteSpace(saved.DeviceId))
            saved.DeviceId = reportedDeviceId;
        if (!string.IsNullOrWhiteSpace(reportedHostname))
            saved.Hostname = reportedHostname;
        saved.Ip = ip;
        saved.Port = port;
    }
}

public static class OnboardingPolicy
{
    public static bool RequiresReplacement(PairStatus status, string controllerId)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Paired &&
            !string.Equals(status.ControllerId, controllerId, StringComparison.Ordinal);
    }

    public static bool CanProceedWithPairing(
        PairStatus status,
        string controllerId,
        bool replacementConfirmed) =>
        !RequiresReplacement(status, controllerId) || replacementConfirmed;

    public static bool CanReuseCredential(
        PairStatus? status,
        string controllerId,
        bool hasCredential) =>
        status is not null &&
        status.Paired &&
        hasCredential &&
        string.Equals(status.ControllerId, controllerId, StringComparison.Ordinal);

    public static bool CanSubmit(
        string ssid,
        string password,
        string pin,
        PairStatus? status,
        string controllerId,
        bool hasCredential)
    {
        var validPin = pin.Length == 8 && pin.All(char.IsDigit);
        return !string.IsNullOrWhiteSpace(ssid) &&
            !string.IsNullOrEmpty(password) &&
            (validPin || CanReuseCredential(
                status, controllerId, hasCredential));
    }

    public static void ValidatePairResult(PairStatus before, PairResult result)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(result);
        if (!string.Equals(before.DeviceId, result.DeviceId, StringComparison.Ordinal))
            throw new InvalidDataException("The Pi identity changed while pairing.");
    }
}

namespace PiSignage.Signage;

public static class DeviceIdentityPolicy
{
    public static bool IsMatch(
        SavedDevice saved,
        string reportedDeviceId,
        string reportedHostname)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (!string.IsNullOrWhiteSpace(saved.DeviceId))
        {
            // once verified, only the same stable identity is acceptable —
            // an agent reporting no device_id never downgrades this check
            return string.Equals(
                saved.DeviceId,
                reportedDeviceId,
                StringComparison.Ordinal);
        }
        // never-verified entry: hostname is the only identity we have; agents
        // older than 2026.07.25.1 report no device_id at all
        return !string.IsNullOrWhiteSpace(saved.Hostname) &&
            !string.IsNullOrWhiteSpace(reportedHostname) &&
            string.Equals(
                saved.Hostname,
                reportedHostname,
                StringComparison.OrdinalIgnoreCase);
    }

    public static void ApplyVerifiedEndpoint(
        SavedDevice saved,
        string reportedDeviceId,
        string reportedHostname,
        string ip,
        int port)
    {
        if (!IsMatch(saved, reportedDeviceId, reportedHostname))
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

    /// <summary>Why USB setup cannot work on this machine, or null when it can.
    /// The Pi's USB network needs Windows 11's in-box NCM driver — on older
    /// Windows the Pi enumerates as an unknown device and no amount of cable
    /// swapping helps, so the wizard must say so instead of blaming the cable.</summary>
    public static string? UsbSetupBlocker(Version windowsVersion)
    {
        ArgumentNullException.ThrowIfNull(windowsVersion);
        var isWindows11OrNewer =
            windowsVersion.Major > 10 ||
            (windowsVersion.Major == 10 && windowsVersion.Build >= 22000);
        return isWindows11OrNewer
            ? null
            : "This computer's version of Windows can't connect to the Pi over " +
              "USB — that needs Windows 11. Run this one-time Pi setup from a " +
              "Windows 11 computer (the computer that sets up the Pi becomes " +
              "its control computer).";
    }

    public static void ValidatePairResult(PairStatus before, PairResult result)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(result);
        if (!string.Equals(before.DeviceId, result.DeviceId, StringComparison.Ordinal))
            throw new InvalidDataException("The Pi identity changed while pairing.");
    }
}

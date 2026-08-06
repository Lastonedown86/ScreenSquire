using PiSignage.Signage;
using Xunit;

public class OnboardingPolicyTests
{
    [Fact]
    public void Stable_saved_identity_rejects_a_different_reported_device()
    {
        var saved = new SavedDevice {
            DeviceId = "expected-id", Name = "Front", Hostname = "front",
            Ip = "192.168.1.20", Port = 8080
        };

        Assert.False(DeviceIdentityPolicy.IsMatch(
            saved, "different-id", "front"));
        Assert.Throws<InvalidDataException>(() =>
            DeviceIdentityPolicy.ApplyVerifiedEndpoint(
                saved, "different-id", "impostor", "192.168.1.99", 9123));
        Assert.Equal("expected-id", saved.DeviceId);
        Assert.Equal("192.168.1.20", saved.Ip);
        Assert.Equal(8080, saved.Port);
    }

    [Fact]
    public void Verified_legacy_device_can_adopt_its_first_stable_identity()
    {
        var saved = new SavedDevice {
            DeviceId = "", Name = "Front", Hostname = "front",
            Ip = "192.168.1.20", Port = 8080
        };

        DeviceIdentityPolicy.ApplyVerifiedEndpoint(
            saved, "device-id", "FRONT", "192.168.1.30", 9123);

        Assert.Equal("device-id", saved.DeviceId);
        Assert.Equal("FRONT", saved.Hostname);
        Assert.Equal("192.168.1.30", saved.Ip);
        Assert.Equal(9123, saved.Port);
    }

    [Fact]
    public void Legacy_device_rejects_a_stale_ip_reporting_another_hostname()
    {
        var saved = new SavedDevice {
            DeviceId = "", Name = "Front", Hostname = "pi-front",
            Ip = "192.168.1.20", Port = 8080
        };

        Assert.False(DeviceIdentityPolicy.IsMatch(
            saved, "other-device-id", "pi-back"));
        Assert.Throws<InvalidDataException>(() =>
            DeviceIdentityPolicy.ApplyVerifiedEndpoint(
                saved,
                "other-device-id",
                "pi-back",
                "192.168.1.20",
                9123));
        Assert.Equal("", saved.DeviceId);
        Assert.Equal("pi-front", saved.Hostname);
        Assert.Equal("192.168.1.20", saved.Ip);
        Assert.Equal(8080, saved.Port);
    }

    [Fact]
    public void Legacy_agent_reporting_no_device_id_matches_a_saved_device_by_hostname()
    {
        var saved = new SavedDevice {
            DeviceId = "", Name = "TV1", Hostname = "TV1",
            Ip = "192.168.0.58", Port = 8080
        };

        Assert.True(DeviceIdentityPolicy.IsMatch(saved, "", "tv1"));

        DeviceIdentityPolicy.ApplyVerifiedEndpoint(
            saved, "", "TV1", "192.168.0.60", 8080);

        Assert.Equal("", saved.DeviceId);
        Assert.Equal("192.168.0.60", saved.Ip);
    }

    [Fact]
    public void Legacy_agent_reporting_no_device_id_still_needs_a_hostname_match()
    {
        var saved = new SavedDevice {
            DeviceId = "", Name = "TV1", Hostname = "TV1",
            Ip = "192.168.0.58", Port = 8080
        };

        Assert.False(DeviceIdentityPolicy.IsMatch(saved, "", "other-tv"));
        Assert.False(DeviceIdentityPolicy.IsMatch(saved, "", ""));
    }

    [Fact]
    public void Verified_device_rejects_an_agent_reporting_no_identity()
    {
        var saved = new SavedDevice {
            DeviceId = "expected-id", Name = "Front", Hostname = "front",
            Ip = "192.168.1.20", Port = 8080
        };

        Assert.False(DeviceIdentityPolicy.IsMatch(saved, "", "front"));
    }

    [Fact]
    public void Replacement_decline_blocks_pairing_to_a_different_controller()
    {
        var status = new PairStatus("device-id", true, "other-controller");

        Assert.True(OnboardingPolicy.RequiresReplacement(status, "this-controller"));
        Assert.False(OnboardingPolicy.CanProceedWithPairing(
            status, "this-controller", replacementConfirmed: false));
        Assert.True(OnboardingPolicy.CanProceedWithPairing(
            status, "this-controller", replacementConfirmed: true));
    }

    [Fact]
    public void Retained_same_controller_credential_allows_retry_without_pin()
    {
        var status = new PairStatus("device-id", true, "this-controller");

        Assert.True(OnboardingPolicy.CanSubmit(
            "Shop", "secret", "", status, "this-controller", hasCredential: true));
        Assert.False(OnboardingPolicy.CanSubmit(
            "Shop", "secret", "", status, "this-controller", hasCredential: false));
        Assert.True(OnboardingPolicy.CanSubmit(
            "Shop", "secret", "12345678", status, "this-controller", hasCredential: false));
    }

    [Fact]
    public void Pair_result_must_match_the_pre_pair_device_identity()
    {
        var status = new PairStatus("expected-id", false, null);
        var result = new PairResult(
            "different-id",
            "this-controller",
            Enumerable.Repeat((byte)1, 32).ToArray());

        Assert.Throws<InvalidDataException>(() =>
            OnboardingPolicy.ValidatePairResult(status, result));
    }

    [Theory]
    [InlineData(10, 0, 19045)]   // Windows 10 22H2
    [InlineData(10, 0, 19044)]
    [InlineData(6, 3, 9600)]     // Windows 8.1
    public void Usb_setup_is_blocked_on_windows_without_a_native_ncm_driver(
        int major, int minor, int build)
    {
        var blocker = OnboardingPolicy.UsbSetupBlocker(
            new Version(major, minor, build));

        Assert.NotNull(blocker);
        Assert.Contains("Windows 11", blocker);
        // it must not read like a cable problem — that's the wrong advice here
        Assert.DoesNotContain("cable", blocker, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(10, 0, 22000)]   // Windows 11 initial release
    [InlineData(10, 0, 26100)]   // Windows 11 24H2
    [InlineData(11, 0, 1)]       // a future major version
    public void Usb_setup_is_allowed_on_windows_11_or_newer(
        int major, int minor, int build)
    {
        Assert.Null(OnboardingPolicy.UsbSetupBlocker(
            new Version(major, minor, build)));
    }
}

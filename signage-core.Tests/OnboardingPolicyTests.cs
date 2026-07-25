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
}

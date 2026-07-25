using PiSignage.Control;
using PiSignage.Signage;
using Xunit;

public class DeliveryPreparationTests
{
    [Fact]
    public async Task RunAsync_uses_fixed_usb_endpoint_then_cleans_local_state_in_order()
    {
        var operations = new RecordingOperations();
        var preparation = new DeliveryPreparation(operations);
        var device = Device("selected-device");
        var context = Context("selected-device");

        await preparation.RunAsync(device, context);

        Assert.Equal(
            new[]
            {
                "status:http://10.55.0.1:8080",
                "reset:http://10.55.0.1:8080",
                "credential:selected-device",
                "device:selected-device",
                "settings:selected-device",
                "cache:selected-device",
            },
            operations.Events);
        Assert.Same(context, operations.ResetContext);
        Assert.Same(device, operations.CachedDevice);
    }

    [Fact]
    public async Task RunAsync_rejects_a_usb_endpoint_reporting_another_device()
    {
        var operations = new RecordingOperations
        {
            PairStatus = new PairStatus(
                "different-device",
                true,
                "builder-controller"),
        };
        var preparation = new DeliveryPreparation(operations);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => preparation.RunAsync(
                Device("selected-device"),
                Context("selected-device")));

        Assert.Contains("different device", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            new[] { "status:http://10.55.0.1:8080" },
            operations.Events);
    }

    [Fact]
    public async Task RunAsync_preserves_every_local_record_when_signed_reset_fails()
    {
        var operations = new RecordingOperations
        {
            ResetFailure = new HttpRequestException("reset failed"),
        };
        var preparation = new DeliveryPreparation(operations);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => preparation.RunAsync(
                Device("selected-device"),
                Context("selected-device")));

        Assert.Equal(
            new[]
            {
                "status:http://10.55.0.1:8080",
                "reset:http://10.55.0.1:8080",
            },
            operations.Events);
    }

    [Fact]
    public async Task RunAsync_rejects_control_credentials_for_another_saved_device()
    {
        var operations = new RecordingOperations();
        var preparation = new DeliveryPreparation(operations);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => preparation.RunAsync(
                Device("selected-device"),
                Context("other-device")));

        Assert.Empty(operations.Events);
    }

    static SavedDevice Device(string deviceId) => new()
    {
        DeviceId = deviceId,
        Name = "Front TV",
        Hostname = "front-pi",
        Ip = "192.168.1.50",
        Port = 8080,
    };

    static ControlContext Context(string deviceId)
    {
        long counter = 0;
        return new ControlContext(
            deviceId,
            "builder-controller",
            Enumerable.Repeat((byte)7, 32).ToArray(),
            () => Interlocked.Increment(ref counter));
    }

    sealed class RecordingOperations : IDeliveryPreparationOperations
    {
        public List<string> Events { get; } = new();
        public PairStatus PairStatus { get; set; } =
            new("selected-device", true, "builder-controller");
        public Exception? ResetFailure { get; set; }
        public ControlContext? ResetContext { get; private set; }
        public SavedDevice? CachedDevice { get; private set; }

        public Task<PairStatus> GetPairStatusAsync(
            string baseUrl,
            CancellationToken cancellationToken)
        {
            Events.Add($"status:{baseUrl}");
            return Task.FromResult(PairStatus);
        }

        public Task<DeliveryResetResult> SendResetAsync(
            string baseUrl,
            ControlContext context,
            CancellationToken cancellationToken)
        {
            Events.Add($"reset:{baseUrl}");
            ResetContext = context;
            if (ResetFailure is not null)
                return Task.FromException<DeliveryResetResult>(ResetFailure);
            return Task.FromResult(new DeliveryResetResult(true, PairStatus.DeviceId));
        }

        public void RemoveCredential(string deviceId) =>
            Events.Add($"credential:{deviceId}");

        public void RemoveSavedDevice(string deviceId) =>
            Events.Add($"device:{deviceId}");

        public void RemoveSignageDeviceId(string deviceId) =>
            Events.Add($"settings:{deviceId}");

        public void ClearThumbnails(SavedDevice device)
        {
            Events.Add($"cache:{device.DeviceId}");
            CachedDevice = device;
        }
    }
}

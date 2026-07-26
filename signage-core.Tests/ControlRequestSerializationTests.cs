using System.Net;
using System.Text;
using PiSignage.Signage;

namespace signage_core.Tests;

public class ControlRequestSerializationTests
{
    [Fact]
    public async Task Same_device_serializes_across_client_instances_and_mutation_families()
    {
        var firstEntered = NewSignal();
        var releaseFirst = NewSignal();
        var secondEntered = NewSignal();
        var counter = 0L;
        var context = Context(
            "same-device",
            () => Interlocked.Increment(ref counter));
        var push = new PushClient(new HttpClient(new ControlledHandler(
            firstEntered,
            releaseFirst,
            """{"ok":true}""")));
        var wifi = new WifiProvisioner(new HttpClient(new ControlledHandler(
            secondEntered,
            release: null,
            """{"ok":true,"connected":true,"ip":"192.168.1.2"}""")));

        var first = push.PostDashboardAsync(
            "http://pi-one:8080",
            new { timer = new { state = "stopped" } },
            context);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = wifi.ConnectAsync(
            "http://pi-two:8080",
            "Shop",
            "password",
            context);

        var raced = await Task.WhenAny(
            secondEntered.Task,
            Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.NotSame(secondEntered.Task, raced);
        Assert.Equal(1, Volatile.Read(ref counter));

        releaseFirst.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, Volatile.Read(ref counter));
    }

    [Fact]
    public async Task Different_devices_proceed_concurrently()
    {
        var firstEntered = NewSignal();
        var releaseFirst = NewSignal();
        var secondEntered = NewSignal();
        var push = new PushClient(new HttpClient(new ControlledHandler(
            firstEntered,
            releaseFirst,
            """{"ok":true}""")));
        var wifi = new WifiProvisioner(new HttpClient(new ControlledHandler(
            secondEntered,
            release: null,
            """{"ok":true,"connected":true,"ip":"192.168.1.3"}""")));

        var first = push.PostDashboardAsync(
            "http://pi-one:8080",
            new { timer = new { state = "stopped" } },
            Context("device-one"));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = wifi.ConnectAsync(
            "http://pi-two:8080",
            "Shop",
            "password",
            Context("device-two"));

        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await second;
        Assert.False(first.IsCompleted);

        releaseFirst.SetResult();
        await first;
    }

    [Fact]
    public async Task Send_waits_for_cross_process_device_lock_before_allocating_counter()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"pisignage-send-lock-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var vaultPath = Path.Combine(root, "credentials.dat");
            const string deviceId = "cross-process-lock-device";
            var lockPath = ControlSendLock.PathFor(vaultPath, deviceId);
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            using var held = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            var counter = 0L;
            var entered = NewSignal();
            var client = new HttpClient(new ControlledHandler(
                entered,
                release: null,
                """{"ok":true}"""));
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                "http://pi:8080/api/next");
            var context = new ControlContext(
                deviceId,
                "test-controller",
                Enumerable.Repeat((byte)1, 32).ToArray(),
                () => Interlocked.Increment(ref counter),
                vaultPath);

            var sending = SignedControlRequest.SendAsync(
                client,
                request,
                context,
                Array.Empty<byte>());
            await Task.Delay(250);

            Assert.False(sending.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref counter));
            Assert.DoesNotContain(deviceId, Path.GetFileName(lockPath));

            held.Dispose();
            await sending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, Volatile.Read(ref counter));
            Assert.Empty(File.ReadAllBytes(lockPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    static ControlContext Context(
        string deviceId,
        Func<long>? takeNextCounter = null)
    {
        long counter = 0;
        return new ControlContext(
            deviceId,
            "test-controller",
            Enumerable.Repeat((byte)1, 32).ToArray(),
            takeNextCounter ?? (() => Interlocked.Increment(ref counter)));
    }

    sealed class ControlledHandler(
        TaskCompletionSource entered,
        TaskCompletionSource? release,
        string responseBody)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            if (release is not null)
                await release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseBody,
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}

using PiSignage.Signage;
using Xunit;

public class DetectionRunGateTests
{
    [Fact]
    public void Cancel_stops_the_active_linked_run_and_blocks_overlap_until_complete()
    {
        using var lifetime = new CancellationTokenSource();
        using var gate = new DetectionRunGate();

        Assert.True(gate.TryBegin(lifetime.Token, out var first));
        Assert.False(gate.TryBegin(lifetime.Token, out _));

        gate.Cancel();

        Assert.True(first.IsCancellationRequested);
        Assert.False(gate.TryBegin(lifetime.Token, out _));

        gate.Complete(first);
        Assert.True(gate.TryBegin(lifetime.Token, out var second));
        Assert.False(second.IsCancellationRequested);
        gate.Complete(second);
    }

    [Fact]
    public void Window_lifetime_cancellation_flows_to_the_active_run()
    {
        using var lifetime = new CancellationTokenSource();
        using var gate = new DetectionRunGate();
        Assert.True(gate.TryBegin(lifetime.Token, out var run));

        lifetime.Cancel();

        Assert.True(run.IsCancellationRequested);
        gate.Complete(run);
    }
}

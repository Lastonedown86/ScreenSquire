using PiSignage.Signage;
using Xunit;

public class RoundTimerTests
{
    [Fact]
    public void StartSetsRunningWithSecondsLabelRound()
    {
        var t = new RoundTimer();
        t.Start(25, "Round 1", 1);
        Assert.Equal(TimerRunState.Running, t.State);
        Assert.Equal(1500, t.RemainingSeconds);
        Assert.Equal("Round 1", t.Label);
        Assert.Equal(1, t.Round);
    }

    [Fact]
    public void PauseResumeKeepsRemaining()
    {
        var t = new RoundTimer(); t.Start(25, "R1", 1);
        t.Pause(600);
        Assert.Equal(TimerRunState.Paused, t.State);
        Assert.Equal(600, t.RemainingSeconds);
        t.Resume(600);
        Assert.Equal(TimerRunState.Running, t.State);
    }

    [Fact]
    public void StopClears()
    {
        var t = new RoundTimer(); t.Start(25, "R1", 1);
        t.Stop();
        Assert.Equal(TimerRunState.Stopped, t.State);
        Assert.Null(t.RemainingSeconds);
    }
}

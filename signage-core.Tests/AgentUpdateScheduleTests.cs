using PiSignage.Signage;

namespace signage_core.Tests;

public class AgentUpdateScheduleTests
{
    static DateTime At(int day, int hour, int minute = 0) =>
        new(2026, 7, day, hour, minute, 0, DateTimeKind.Local);

    [Fact]
    public void Runs_inside_the_window_when_it_has_never_run()
    {
        Assert.True(AgentUpdateSchedule.ShouldRun(At(20, 3, 0), null));
    }

    [Theory]
    [InlineData(2, 59)]   // just before the window opens
    [InlineData(5, 0)]    // the window is half-open: 05:00 is already shut
    [InlineData(5, 1)]
    [InlineData(15, 0)]   // trading hours — the entire point of this class
    [InlineData(23, 30)]
    public void Never_runs_outside_the_window(int hour, int minute)
    {
        Assert.False(AgentUpdateSchedule.ShouldRun(At(20, hour, minute), null));
    }

    [Fact]
    public void Runs_at_the_moment_the_window_opens()
    {
        Assert.True(AgentUpdateSchedule.ShouldRun(At(20, 3, 0), At(19, 3, 30)));
    }

    [Fact]
    public void Does_not_run_twice_in_one_night()
    {
        // A sweep completed at 03:10; a tick at 03:25 must leave the TVs alone.
        Assert.False(AgentUpdateSchedule.ShouldRun(At(20, 3, 25), At(20, 3, 10)));
    }

    [Fact]
    public void Runs_again_the_next_night()
    {
        Assert.True(AgentUpdateSchedule.ShouldRun(At(21, 3, 5), At(20, 3, 10)));
    }

    [Fact]
    public void A_sweep_from_earlier_the_same_day_does_not_close_tonight()
    {
        // The manual button can complete a sweep at any hour. Yesterday's 03:10 run
        // is what closes a night; a run at 14:00 today happened after that window
        // opened, so it must not suppress tomorrow morning's sweep either.
        Assert.True(AgentUpdateSchedule.ShouldRun(At(21, 3, 5), At(20, 14, 0)));
    }

    [Fact]
    public void The_window_is_wide_enough_for_several_attempts()
    {
        // An unreachable Pi leaves the night open, so the retry budget is real:
        // the sweep must get more than one shot before the window shuts.
        var attempts = (AgentUpdateSchedule.WindowEnd - AgentUpdateSchedule.WindowStart)
            / AgentUpdateSchedule.CheckInterval;
        Assert.True(attempts >= 4, $"only {attempts} attempts fit in the window");
    }

    [Fact]
    public void The_window_closes_before_the_pis_own_restart_timer_matters()
    {
        // pi-setup/install.sh runs pisignage-kiosk-restart.timer at 04:00. Starting
        // at 03:00 means a push finishes well before it, so the TV blinks once
        // rather than twice.
        Assert.Equal(TimeSpan.FromHours(3), AgentUpdateSchedule.WindowStart);
        Assert.True(AgentUpdateSchedule.WindowStart < TimeSpan.FromHours(4));
    }
}

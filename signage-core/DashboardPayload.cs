namespace PiSignage.Signage;

public static class DashboardPayload
{
    public static object Build(DashboardState state, RoundTimer timer, string? sound = null) => new
    {
        view_data = new { boards = state.Boards },
        timer = new
        {
            state = timer.State.ToString().ToLowerInvariant(),
            remaining = timer.RemainingSeconds,
            round = timer.Round,
            label = timer.Label,
            sound,
        },
    };

    // Timer-only push: view_data omitted entirely so the agent's board merge
    // keeps whatever boards each TV already has (boards travel only with uploads).
    public static object BuildTimerOnly(RoundTimer timer, string? sound = null) => new
    {
        timer = new
        {
            state = timer.State.ToString().ToLowerInvariant(),
            remaining = timer.RemainingSeconds,
            round = timer.Round,
            label = timer.Label,
            sound,
        },
    };
}

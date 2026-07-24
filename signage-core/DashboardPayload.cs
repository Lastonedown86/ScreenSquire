namespace PiSignage.Signage;

public static class DashboardPayload
{
    public static object Build(DashboardState state, RoundTimer timer) => new
    {
        view_data = new { boards = state.Boards },
        timer = new
        {
            state = timer.State.ToString().ToLowerInvariant(),
            remaining = timer.RemainingSeconds,
            round = timer.Round,
            label = timer.Label,
        },
    };

    // Timer-only push: view_data omitted entirely so the agent's board merge
    // keeps whatever boards each TV already has (boards travel only with uploads).
    public static object BuildTimerOnly(RoundTimer timer) => new
    {
        timer = new
        {
            state = timer.State.ToString().ToLowerInvariant(),
            remaining = timer.RemainingSeconds,
            round = timer.Round,
            label = timer.Label,
        },
    };
}

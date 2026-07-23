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
}

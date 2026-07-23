namespace PiSignage.Signage;

public sealed class DashboardState
{
    // slot name (e.g. "pairings", "standings") -> media path (e.g. "/media/pairings-2.png")
    public Dictionary<string, string> Boards { get; } = new();
}

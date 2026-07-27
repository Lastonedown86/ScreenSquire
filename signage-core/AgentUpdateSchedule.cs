namespace PiSignage.Signage;

/// <summary>Decides when the Controller laptop may push agent updates to the
/// Display Pis on its own.
///
/// Updating a Pi restarts its kiosk browser, so the TV goes dark for roughly
/// half a minute. That is unacceptable during trading hours and unremarkable at
/// 03:00, which is the whole reason this exists rather than pushing on
/// launch.</summary>
public static class AgentUpdateSchedule
{
    /// <summary>Opens after the store is long closed and closes before the Pi's
    /// own 04:00 kiosk-restart timer has had time to matter.</summary>
    public static readonly TimeSpan WindowStart = TimeSpan.FromHours(3);
    public static readonly TimeSpan WindowEnd = TimeSpan.FromHours(5);

    /// <summary>How often to reconsider. Well inside the window, so a Pi that is
    /// switched on late still gets several chances before the window shuts.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    /// <summary>May a sweep start now?
    ///
    /// Local time on purpose: "the middle of the night" is a property of the
    /// store, not of UTC.</summary>
    // ponytail: assumes a window that does not cross midnight, which 03:00-05:00
    // does not. Handle wrapping only if the window is ever made configurable.
    public static bool ShouldRun(
        DateTime nowLocal,
        DateTime? lastCompletedLocal,
        TimeSpan? windowStart = null,
        TimeSpan? windowEnd = null)
    {
        var start = windowStart ?? WindowStart;
        var end = windowEnd ?? WindowEnd;

        var time = nowLocal.TimeOfDay;
        if (time < start || time >= end) return false;

        // Once per opening of the window. A sweep that finished earlier tonight
        // closes it; one that finished before tonight's window opened does not,
        // which is what makes an unreachable Pi retry within the same night.
        if (lastCompletedLocal is not DateTime last) return true;
        return last < nowLocal.Date + start;
    }
}

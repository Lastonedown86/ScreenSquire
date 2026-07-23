namespace PiSignage.Signage;

public enum TimerRunState { Stopped, Running, Paused }

public sealed class RoundTimer
{
    public TimerRunState State { get; private set; } = TimerRunState.Stopped;
    public int? RemainingSeconds { get; private set; }
    public string? Label { get; private set; }
    public int? Round { get; private set; }

    public void Start(int minutes, string label, int round)
    { State = TimerRunState.Running; RemainingSeconds = minutes * 60; Label = label; Round = round; }

    public void Pause(int remainingSeconds)
    { State = TimerRunState.Paused; RemainingSeconds = remainingSeconds; }

    public void Resume(int remainingSeconds)
    { State = TimerRunState.Running; RemainingSeconds = remainingSeconds; }

    public void Stop()
    { State = TimerRunState.Stopped; RemainingSeconds = null; }
}

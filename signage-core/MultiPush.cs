namespace PiSignage.Signage;

public sealed record PushTarget(
    string Name,
    string BaseUrl,
    ControlContext ControlContext);

public sealed class MultiPushResult
{
    public List<string> Succeeded { get; } = new();
    public List<(string Name, string Error)> Failed { get; } = new();
    public bool AllFailed => Succeeded.Count == 0 && Failed.Count > 0;

    // Client-facing toast text: "Front updated. Back unreachable — that TV was not updated."
    public string Summary(string verb = "updated")
    {
        var parts = new List<string>();
        if (Succeeded.Count > 0)
            parts.Add(string.Join(", ", Succeeded) + $" {verb}.");
        foreach (var (name, _) in Failed)
            parts.Add($"{name} unreachable — that TV was not {verb}.");
        return string.Join(" ", parts);
    }
}

public static class MultiPush
{
    // Sequential on purpose: 4 LAN targets, and sequential keeps error handling dead simple.
    // ponytail: parallelize only if the shop ever runs enough TVs for it to matter.
    public static async Task<MultiPushResult> RunAsync(
        IEnumerable<PushTarget> targets, Func<PushTarget, Task> action)
    {
        var result = new MultiPushResult();
        foreach (var t in targets)
        {
            try { await action(t); result.Succeeded.Add(t.Name); }
            catch (Exception ex) { result.Failed.Add((t.Name, ex.Message)); }
        }
        return result;
    }
}

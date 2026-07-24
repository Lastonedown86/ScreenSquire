using System.Text.Json;
using PiSignage.Signage;
using Xunit;

public class DashboardPayloadTests
{
    [Fact]
    public void BuildMatchesWireShape()
    {
        var state = new DashboardState();
        state.Boards["pairings"] = "/media/pairings-2.png";
        var timer = new RoundTimer(); timer.Start(25, "Round 3", 3);

        var json = JsonSerializer.Serialize(DashboardPayload.Build(state, timer));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("/media/pairings-2.png",
            root.GetProperty("view_data").GetProperty("boards").GetProperty("pairings").GetString());
        Assert.Equal("running", root.GetProperty("timer").GetProperty("state").GetString());
        Assert.Equal(1500, root.GetProperty("timer").GetProperty("remaining").GetInt32());
        Assert.Equal(3, root.GetProperty("timer").GetProperty("round").GetInt32());
        Assert.Equal("Round 3", root.GetProperty("timer").GetProperty("label").GetString());
    }

    [Fact]
    public void StoppedTimerSerializesNullRemaining()
    {
        var json = JsonSerializer.Serialize(DashboardPayload.Build(new DashboardState(), new RoundTimer()));
        using var doc = JsonDocument.Parse(json);
        var timer = doc.RootElement.GetProperty("timer");
        Assert.Equal("stopped", timer.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, timer.GetProperty("remaining").ValueKind);
    }

    [Fact]
    public void PausedTimerSerializesStateAndRemaining()
    {
        var timer = new RoundTimer(); timer.Start(25, "Round 3", 3); timer.Pause(843);
        var json = JsonSerializer.Serialize(DashboardPayload.Build(new DashboardState(), timer));
        using var doc = JsonDocument.Parse(json);
        var t = doc.RootElement.GetProperty("timer");
        Assert.Equal("paused", t.GetProperty("state").GetString());
        Assert.Equal(843, t.GetProperty("remaining").GetInt32());
    }

    [Fact]
    public void BuildTimerOnlyOmitsViewDataAndCarriesTimerFields()
    {
        var timer = new RoundTimer(); timer.Start(25, "Round 3", 3);
        var json = JsonSerializer.Serialize(DashboardPayload.BuildTimerOnly(timer));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("view_data", out _));
        var t = root.GetProperty("timer");
        Assert.Equal("running", t.GetProperty("state").GetString());
        Assert.Equal(1500, t.GetProperty("remaining").GetInt32());
        Assert.Equal(3, t.GetProperty("round").GetInt32());
        Assert.Equal("Round 3", t.GetProperty("label").GetString());
    }
}

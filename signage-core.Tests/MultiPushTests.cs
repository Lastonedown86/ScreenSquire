using System.Net.Http;
using PiSignage.Signage;
using Xunit;

public class MultiPushTests
{
    static PushTarget T(string name) => new(name, "http://" + name + ":8080");

    [Fact]
    public async Task AllSucceed()
    {
        var r = await MultiPush.RunAsync(new[] { T("Front"), T("Back") }, _ => Task.CompletedTask);
        Assert.Equal(new[] { "Front", "Back" }, r.Succeeded);
        Assert.Empty(r.Failed);
        Assert.False(r.AllFailed);
    }

    [Fact]
    public async Task OneFailureDoesNotBlockTheRest()
    {
        var r = await MultiPush.RunAsync(new[] { T("Front"), T("Back"), T("Side") },
            t => t.Name == "Back" ? Task.FromException(new HttpRequestException("timeout")) : Task.CompletedTask);
        Assert.Equal(new[] { "Front", "Side" }, r.Succeeded);
        Assert.Single(r.Failed);
        Assert.Equal("Back", r.Failed[0].Name);
    }

    [Fact]
    public async Task SummaryNamesSuccessesAndFailuresInClientLanguage()
    {
        var r = await MultiPush.RunAsync(new[] { T("Front"), T("Back") },
            t => t.Name == "Back" ? Task.FromException(new HttpRequestException("x")) : Task.CompletedTask);
        Assert.Equal("Front updated. Back unreachable — that TV was not updated.", r.Summary());
    }

    [Fact]
    public async Task AllFailedFlag()
    {
        var r = await MultiPush.RunAsync(new[] { T("Front") },
            _ => Task.FromException(new HttpRequestException("x")));
        Assert.True(r.AllFailed);
    }
}

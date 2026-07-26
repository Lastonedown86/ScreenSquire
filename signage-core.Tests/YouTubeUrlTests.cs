using PiSignage.Signage;
using Xunit;

// Mirrors agent/test_normalize_url.py — the two parsers must stay in lockstep.
public class YouTubeUrlTests
{
    const string Id = "2B_L3WsMqTc";

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=2B_L3WsMqTc")]
    [InlineData("https://youtu.be/2B_L3WsMqTc")]
    [InlineData("https://www.youtube.com/watch?feature=share&v=2B_L3WsMqTc")]
    [InlineData("https://www.youtube.com/shorts/2B_L3WsMqTc")]
    [InlineData("https://www.youtube.com/live/2B_L3WsMqTc")]
    [InlineData("https://m.youtube.com/watch?v=2B_L3WsMqTc")]
    [InlineData("https://www.youtube.com/embed/2B_L3WsMqTc")]
    public void RecognizedFormsYieldTheId(string url)
        => Assert.Equal(Id, YouTubeUrl.TryGetVideoId(url));

    [Theory]
    [InlineData("https://notyoutube.com/watch?v=2B_L3WsMqTc")]
    [InlineData("https://youtube.com.evil.example/watch?v=2B_L3WsMqTc")]
    [InlineData("javascript:youtube.com/watch?v=2B_L3WsMqTc")]
    [InlineData("https://www.youtube.com/watch?v=2B_L3WsMqTcX")]   // 12 chars
    [InlineData("https://www.youtube.com/watch?v=a&v=2B_L3WsMqTc")] // ambiguous
    [InlineData("https://example.com/page")]
    [InlineData("not a url")]
    [InlineData("")]
    public void LookalikesAndJunkAreRejected(string url)
        => Assert.Null(YouTubeUrl.TryGetVideoId(url));

    [Fact]
    public void OversizedUrlIsRejected()
    {
        var url = "https://www.youtube.com/watch?"
                  + string.Concat(Enumerable.Repeat("feature=share&", 400))
                  + "v=" + Id;
        Assert.True(url.Length > 4096);
        Assert.Null(YouTubeUrl.TryGetVideoId(url));
    }

    [Fact]
    public void WatchUrlRoundTrips()
        => Assert.Equal(Id, YouTubeUrl.TryGetVideoId(YouTubeUrl.WatchUrl(Id)));
}

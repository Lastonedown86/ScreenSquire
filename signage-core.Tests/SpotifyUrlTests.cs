using PiSignage.Signage;
using Xunit;

public class SpotifyUrlTests
{
    const string Id = "4uLU6hMCjMI75M1A2tKUQC";   // 22 base62 chars

    [Theory]
    [InlineData("https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC", "track")]
    [InlineData("https://open.spotify.com/album/4uLU6hMCjMI75M1A2tKUQC", "album")]
    [InlineData("https://open.spotify.com/playlist/4uLU6hMCjMI75M1A2tKUQC", "playlist")]
    [InlineData("https://open.spotify.com/episode/4uLU6hMCjMI75M1A2tKUQC", "episode")]
    [InlineData("https://open.spotify.com/show/4uLU6hMCjMI75M1A2tKUQC", "show")]
    [InlineData("https://open.spotify.com/artist/4uLU6hMCjMI75M1A2tKUQC", "artist")]
    [InlineData("https://open.spotify.com/intl-de/track/4uLU6hMCjMI75M1A2tKUQC", "track")]
    [InlineData("https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC?si=abc123", "track")]
    [InlineData("spotify:track:4uLU6hMCjMI75M1A2tKUQC", "track")]
    [InlineData("spotify:show:4uLU6hMCjMI75M1A2tKUQC", "show")]
    public void RecognizedFormsParse(string input, string expectedType)
    {
        var got = SpotifyUrl.TryParse(input);
        Assert.NotNull(got);
        Assert.Equal(expectedType, got!.Value.Type);
        Assert.Equal(Id, got.Value.Id);
    }

    [Theory]
    [InlineData("https://open.spotify.com.evil.example/track/4uLU6hMCjMI75M1A2tKUQC")]
    [InlineData("https://notspotify.com/track/4uLU6hMCjMI75M1A2tKUQC")]
    [InlineData("javascript:open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC")]
    [InlineData("https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQ")]     // 21 chars
    [InlineData("https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQCX")]   // 23 chars
    [InlineData("https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKU_C")]    // underscore
    [InlineData("https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKU-C")]    // dash
    [InlineData("https://open.spotify.com/user/4uLU6hMCjMI75M1A2tKUQC")]     // unknown type
    [InlineData("https://open.spotify.com/intl-de/4uLU6hMCjMI75M1A2tKUQC")]  // prefix, no type
    [InlineData("spotify:track:short")]
    [InlineData("spotify:bogus:4uLU6hMCjMI75M1A2tKUQC")]
    [InlineData("not a url")]
    [InlineData("")]
    public void LookalikesAndJunkAreRejected(string input)
        => Assert.Null(SpotifyUrl.TryParse(input));

    [Fact]
    public void OversizedUrlIsRejected()
    {
        var url = "https://open.spotify.com/track/" + Id + "?si="
                  + new string('a', 4100);
        Assert.True(url.Length > 4096);
        Assert.Null(SpotifyUrl.TryParse(url));
    }

    [Fact]
    public void HelpersRoundTrip()
    {
        Assert.Equal($"spotify:track:{Id}", SpotifyUrl.CanonicalUri("track", Id));
        Assert.Equal(("track", Id), SpotifyUrl.TryParse(SpotifyUrl.OpenUrl("track", Id)));
        Assert.Equal($"https://open.spotify.com/embed/track/{Id}", SpotifyUrl.EmbedUrl("track", Id));
    }
}

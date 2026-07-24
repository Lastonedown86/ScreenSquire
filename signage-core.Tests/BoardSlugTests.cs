using PiSignage.Signage;
using Xunit;

public class BoardSlugTests
{
    [Theory]
    [InlineData("Top 8 Bracket", "top-8-bracket")]
    [InlineData("pairings", "pairings")]
    [InlineData("  Announcements!  ", "announcements")]
    [InlineData("A -- B", "a-b")]
    [InlineData("!!!", "")]
    public void From(string input, string expected) => Assert.Equal(expected, BoardSlug.From(input));
}

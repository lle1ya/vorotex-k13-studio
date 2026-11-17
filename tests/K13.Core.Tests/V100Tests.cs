using Xunit;

namespace Vorotex.K13.Tests;

/// <summary>1.0.0 — version scheme change: tags v5.x.y were built as assembly 0.5.x.y; 1.0.0 is assembly 1.0.0(.0).</summary>
public class V100Tests
{
    const string Repo = "https://github.com/lle1ya/vorotex-k13-studio/releases/tag/";
    static UpdateCheck.Release FromApi(string tag) =>
        UpdateCheck.Parse($"{{\"tag_name\":\"{tag}\",\"html_url\":\"{Repo}{tag}\",\"draft\":false,\"prerelease\":false}}")!;

    [Theory]
    [InlineData(0, 5, 10, 0)]
    [InlineData(0, 5, 10, 1)]
    [InlineData(0, 5, 9, 8)]
    public void Old_builds_see_1_0_0_as_newer_via_API_and_redirect(int a, int b, int c, int d)
    {
        var old = new Version(a, b, c, d);
        Assert.True(UpdateCheck.IsNewer(FromApi("v1.0.0"), old));
        Assert.True(UpdateCheck.IsNewer(UpdateCheck.ParseLatestRedirect(Repo + "v1.0.0")!, old));
    }

    [Theory]
    [InlineData("v5.10.1")]
    [InlineData("v5.10.0")]
    [InlineData("v5.99.9")]
    [InlineData("v1.0.0")]
    public void Build_1_0_0_does_not_offer_old_or_same_tags(string tag)
    {
        foreach (var cur in new[] { new Version(1, 0, 0), new Version(1, 0, 0, 0) })
        {
            Assert.False(UpdateCheck.IsNewer(FromApi(tag), cur));
            Assert.False(UpdateCheck.IsNewer(UpdateCheck.ParseLatestRedirect(Repo + tag)!, cur));
        }
    }

    [Fact]
    public void Later_1_x_releases_are_offered_to_1_0_0()
    {
        var cur = new Version(1, 0, 0, 0);
        Assert.True(UpdateCheck.IsNewer(FromApi("v1.0.1"), cur));
        Assert.True(UpdateCheck.IsNewer(FromApi("v1.1.0"), cur));
        Assert.True(UpdateCheck.IsNewer(FromApi("v2.0.0"), cur));
        Assert.Equal("v1.0.0", UpdateCheck.Display(new Version(1, 0, 0)));
        Assert.Equal("v1.0.0", UpdateCheck.Display(new Version(1, 0, 0, 0)));
        Assert.Equal("v5.10.1", UpdateCheck.Display(new Version(0, 5, 10, 1)));
    }
}

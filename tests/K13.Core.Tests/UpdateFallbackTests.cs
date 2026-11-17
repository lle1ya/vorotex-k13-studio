using Xunit;

namespace Vorotex.K13.Tests;

/// <summary>Update check fallback (for 1.0): the GitHub API is limited to 60 requests an hour per address — the makers got
/// «403 rate limit exceeded» (17.11.2026 14:17). Then the release page redirect …/releases/latest → …/releases/tag/vX is used.</summary>
public class UpdateFallbackTests
{
    const string Api = "{\"tag_name\":\"v1.0.0\",\"html_url\":\"https://github.com/lle1ya/vorotex-k13-studio/releases/tag/v1.0.0\",\"draft\":false,\"prerelease\":false}";
    static Func<Task<(int, string?)>> Ans(int st, string? s) => () => Task.FromResult<(int, string?)>((st, s));
    static Func<Task<(int, string?)>> Throws(Exception e) => () => Task.FromException<(int, string?)>(e);
    static Func<Task<(int, string?)>> MustNotCall => () => throw new Xunit.Sdk.XunitException("fallback called although the API answered");

    [Fact]
    public void Redirect_location_gives_the_tag()
    {
        var r = UpdateCheck.ParseLatestRedirect("https://github.com/lle1ya/vorotex-k13-studio/releases/tag/v5.10.1")!;
        Assert.Equal("v5.10.1", r.Tag);
        Assert.Equal(new Version(0, 5, 10, 1), r.Version);
        Assert.Equal("https://github.com/lle1ya/vorotex-k13-studio/releases/tag/v5.10.1", r.Url);
        Assert.Equal("v1.0.0", UpdateCheck.ParseLatestRedirect("/lle1ya/vorotex-k13-studio/releases/tag/v1.0.0")!.Tag);   // relative
        Assert.Equal("v1.0.0", UpdateCheck.ParseLatestRedirect("https://github.com/lle1ya/vorotex-k13-studio/releases/tag/v1.0.0?x=1")!.Tag);
        Assert.Null(UpdateCheck.ParseLatestRedirect("https://github.com/lle1ya/vorotex-k13-studio/releases"));            // no release yet
        Assert.Null(UpdateCheck.ParseLatestRedirect("https://github.com/login?return_to=x"));
        Assert.Null(UpdateCheck.ParseLatestRedirect("https://evil.example/lle1ya/vorotex-k13-studio/releases/tag/v9.9.9"));
        Assert.Null(UpdateCheck.ParseLatestRedirect("https://github.com/lle1ya/vorotex-k13-studio/releases/tag/nightly"));
        Assert.Null(UpdateCheck.ParseLatestRedirect(null));
    }

    [Fact]
    public async Task API_answer_is_used_without_the_fallback()
    {
        var o = await UpdateCheck.ResolveAsync(Ans(200, Api), MustNotCall);
        Assert.Equal(UpdateCheck.Source.Api, o.From);
        Assert.Equal("v1.0.0", o.Latest!.Tag);
        Assert.Null(o.Error);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Rate_limit_or_error_falls_back_to_the_release_page(int status)
    {
        var o = await UpdateCheck.ResolveAsync(Ans(status, null), Ans(302, "https://github.com/lle1ya/vorotex-k13-studio/releases/tag/v1.0.0"));
        Assert.Equal(UpdateCheck.Source.Redirect, o.From);
        Assert.Equal("v1.0.0", o.Latest!.Tag);
        Assert.True(UpdateCheck.IsNewer(o.Latest, new Version(0, 5, 10, 1)));
        if (status is 403 or 429) Assert.Contains("лимит", o.Log);
    }

    [Fact]
    public async Task Network_error_or_garbage_from_the_API_falls_back_too()
    {
        var a = await UpdateCheck.ResolveAsync(Throws(new HttpRequestException("No such host")), Ans(301, "https://github.com/lle1ya/vorotex-k13-studio/releases/tag/v5.10.1"));
        Assert.Equal(UpdateCheck.Source.Redirect, a.From);
        var b = await UpdateCheck.ResolveAsync(Ans(200, "<html>Service unavailable</html>"), Ans(302, "https://github.com/lle1ya/vorotex-k13-studio/releases/tag/v5.10.1"));
        Assert.Equal("v5.10.1", b.Latest!.Tag);
        var c = await UpdateCheck.ResolveAsync(Throws(new TaskCanceledException()), Ans(302, "https://github.com/lle1ya/vorotex-k13-studio/releases/tag/v5.10.1"));
        Assert.Equal(UpdateCheck.Source.Redirect, c.From);
    }

    [Fact]
    public async Task Both_failing_gives_a_clear_Russian_message()
    {
        foreach (var fallback in new[] { Ans(403, null), Ans(302, "https://github.com/login"), Ans(200, null), Throws(new HttpRequestException("offline")) })
        {
            var o = await UpdateCheck.ResolveAsync(Ans(403, null), fallback);
            Assert.Null(o.Latest);
            Assert.Equal(UpdateCheck.Source.None, o.From);
            Assert.Equal(UpdateCheck.FailText, o.Error);
            Assert.Contains("Не удалось узнать, есть ли новая версия", o.Error);
            Assert.Contains(UpdateCheck.ReleasesPage, o.Error);
            Assert.Contains("API", o.Log);                                       // both reasons go to the log
            Assert.Contains("страница выпусков", o.Log);
        }
    }

    [Fact]
    public void Schedule_respects_the_setting_and_once_a_day()
    {
        var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(UpdateCheck.AutoCheckDue(false, default, now));          // switched off — never automatic
        Assert.True(UpdateCheck.AutoCheckDue(true, default, now));
        Assert.False(UpdateCheck.AutoCheckDue(true, now.AddHours(-23), now));
        Assert.True(UpdateCheck.AutoCheckDue(true, now.AddHours(-24), now));
    }
}

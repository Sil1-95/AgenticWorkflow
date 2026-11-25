using AgenticWorkflow.Runtime.Herdr.Tests.Live;

namespace AgenticWorkflow.Runtime.Herdr.Tests;

/// <summary>
/// The reaper of an isolated Herdr core a dead controller left behind. It can only ever name AgenticWorkflow's own namespace, and it reports "gone" only when the core was
/// confirmed to answer no more.
/// </summary>
public sealed class HerdrIsolatedCoreTests
{
    private const string OwnSession = "agenticworkflow-aw-0123abcd";

    [Theory]
    [InlineData("agenticworkflow-aw-0123abcd", true)]
    [InlineData("agenticworkflow-aw-0123ABCD", false)]
    [InlineData("agenticworkflow-aw-0123abc", false)]
    [InlineData("agenticworkflow-aw-0123abcde", false)]
    [InlineData("agenticworkflow-aw-0123abcd ", false)]
    [InlineData("agenticworkflow-aw-0123abcd\n", false)]
    [InlineData("agenticworkflow-m0-0123abcd", false)]
    [InlineData("default", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_name_in_the_isolated_namespace_is_one_of_ours(string? name, bool own)
        => Assert.Equal(own, HerdrIsolatedCore.IsOwnSessionName(name));

    [Fact]
    public void A_new_session_name_is_always_in_the_namespace_and_distinct()
    {
        var names = Enumerable.Range(0, 50).Select(_ => HerdrIsolatedCore.NewSessionName()).ToList();

        Assert.All(names, name => Assert.True(HerdrIsolatedCore.IsOwnSessionName(name), name));
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Theory]
    [InlineData("default")]
    [InlineData("my-own-session")]
    [InlineData("agenticworkflow-aw-0123abcd/../x")]
    [InlineData("")]
    public async Task A_session_that_is_not_ours_is_refused_before_anything_is_run(string session)
    {
        // The executable does not exist: if the name were not refused first, running it would fail differently.
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => HerdrIsolatedCore.StopAsync(session, Path.Combine(Path.GetTempPath(), "no-such-hc.exe"), TimeSpan.FromSeconds(5), CancellationToken.None));

        Assert.Equal("session", exception.ParamName);
    }

    [Fact]
    public async Task A_core_that_answers_no_more_after_the_stop_is_confirmed_gone()
    {
        using var fake = new FakeHcDirectory(
            """
            echo %* | findstr /c:"stop" >nul && (echo {"stopped":true}& exit /b 0)
            echo {"error":{"hint":"none","kind":"unavailable","message":"no core"}} 1>&2& exit /b 5
            """);

        Assert.True(await HerdrIsolatedCore.StopAsync(OwnSession, fake.ExecutablePath, TimeSpan.FromSeconds(10), CancellationToken.None));
    }

    [Fact]
    public async Task A_core_that_was_not_there_is_confirmed_gone()
    {
        using var fake = new FakeHcDirectory(
            """
            echo %* | findstr /c:"stop" >nul && (echo {"stopped":false}& exit /b 0)
            echo {"error":{"hint":"none","kind":"unavailable","message":"no core"}} 1>&2& exit /b 5
            """);

        Assert.True(await HerdrIsolatedCore.StopAsync(OwnSession, fake.ExecutablePath, TimeSpan.FromSeconds(10), CancellationToken.None));
    }

    [Fact]
    public async Task A_core_that_still_answers_after_the_stop_is_not_confirmed_gone()
    {
        using var fake = new FakeHcDirectory(
            """
            echo %* | findstr /c:"stop" >nul && (echo {"stopped":false}& exit /b 0)
            echo {"status":"running"}
            """);

        Assert.False(await HerdrIsolatedCore.StopAsync(OwnSession, fake.ExecutablePath, TimeSpan.FromSeconds(10), CancellationToken.None));
    }

    [Fact]
    public async Task A_core_that_cannot_be_asked_is_not_confirmed_gone()
    {
        using var fake = new FakeHcDirectory("""echo {"error":{"hint":"none","kind":"internal","message":"boom"}} 1>&2& exit /b 1""");

        Assert.False(await HerdrIsolatedCore.StopAsync(OwnSession, fake.ExecutablePath, TimeSpan.FromSeconds(10), CancellationToken.None));
    }

    [LiveFact]
    public async Task Live_a_session_that_does_not_exist_is_confirmed_gone_without_starting_a_core()
    {
        var hc = Environment.GetEnvironmentVariable("AGENTICWORKFLOW_LIVE_HC") ?? "hc";

        Assert.True(await HerdrIsolatedCore.StopAsync(HerdrIsolatedCore.NewSessionName(), hc, TimeSpan.FromSeconds(30), CancellationToken.None));
    }
}

using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Runtime.Herdr.Tests;

/// <summary>
/// A harness that needs a person while it is still starting (trust this folder, log in). With nobody attending that fails the start (covered in <see cref="HerdrAgentRuntimeTests"/>);
/// when a person is attending the runtime keeps the session, reports the wait, never answers it, and carries on once the agent says it is ready.
/// </summary>
public sealed class HerdrStartupInteractionTests : IAsyncLifetime
{
    private static readonly ProfileId Profile = new("claude-local");
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

    private readonly FakeHerdrCore _core = new();
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "aw-herdr-startup-" + Guid.NewGuid().ToString("N")[..8]);

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_workspace);

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_workspace, recursive: true);

        return Task.CompletedTask;
    }

    private HerdrAgentRuntime NewRuntime(TimeSpan startup, TimeSpan? person = null)
        => new(
            new HerdrAgentRuntimeOptions
            {
                Session = "agenticworkflow-m2b-unit",
                Profiles = [ClaudeCodeLaunch.Profile(Profile)],
                StartupTimeout = startup,
                StartupInteractionTimeout = person ?? Generous,
                CommandTimeout = Generous,
            },
            _core,
            _core);

    private Task<AgentSessionId> StartAsync(HerdrAgentRuntime runtime, IAgentStartupInteractions? interactions)
        => runtime.StartSessionAsync(new AgentSessionRequest(Profile, _workspace, "task one") { StartupInteractions = interactions });

    private FakeTerminal Ours() => _core.TerminalSnapshot().Single(t => t.Labels.ContainsKey("aw_runtime"));

    private async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Generous;

        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "the condition was never reached");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task A_blocked_agent_at_startup_is_reported_not_answered_and_the_start_completes_when_the_person_has_answered_in_its_own_session()
    {
        _core.StatusAtLaunch = "blocked";
        _core.StatusKindAtLaunch = "permission";
        await using var runtime = NewRuntime(TimeSpan.FromSeconds(30));
        var interactions = new RecordingStartupInteractions();

        var start = StartAsync(runtime, interactions);
        await WaitUntilAsync(() => interactions.Opened.Count == 1);

        // The start is still waiting: the session is alive and visible, and nothing was typed into it.
        Assert.False(start.IsCompleted);
        Assert.Equal("permission", interactions.Opened[0].Kind.ToString().ToLowerInvariant());
        Assert.Equal(0, interactions.Closed);
        Assert.NotNull(interactions.Opened[0].Attach);

        // The person answers in the agent's own session: the harness then reports it is ready.
        Ours().Report("idle", null, null);

        var session = await start.WaitAsync(Generous);

        Assert.NotNull(session.Value);
        Assert.Single(interactions.Opened);
        Assert.Equal(1, interactions.Closed);
        Assert.DoesNotContain("terminal.send", _core.Methods);
        Assert.DoesNotContain("terminal.keys", _core.Methods);
        Assert.DoesNotContain("terminal.kill", _core.Methods);
        Assert.DoesNotContain("terminal.rm", _core.Methods);
        Assert.True(Ours().Live);
    }

    [Fact]
    public async Task An_agent_that_reports_nothing_for_the_whole_startup_budget_is_reported_as_possibly_waiting_for_a_person_and_carries_on_when_it_becomes_ready()
    {
        _core.StatusAtLaunch = null;
        await using var runtime = NewRuntime(TimeSpan.FromMilliseconds(200));
        var interactions = new RecordingStartupInteractions();

        var start = StartAsync(runtime, interactions);
        await WaitUntilAsync(() => interactions.Opened.Count == 1);

        var interaction = interactions.Opened[0];
        Assert.Equal(AgentInteractionKind.Unspecified, interaction.Kind);
        Assert.Contains("has not reported ready", interaction.Description);
        Assert.False(start.IsCompleted);

        Ours().Report("idle", null, null);
        await start.WaitAsync(Generous);

        Assert.Equal(1, interactions.Closed);
        Assert.DoesNotContain("terminal.send", _core.Methods);
    }

    [Fact]
    public async Task A_wait_for_a_person_that_never_ends_fails_the_start_and_cleans_the_session_up()
    {
        _core.StatusAtLaunch = "blocked";
        _core.StatusKindAtLaunch = "permission";
        await using var runtime = NewRuntime(TimeSpan.FromSeconds(30), person: TimeSpan.FromMilliseconds(300));
        var interactions = new RecordingStartupInteractions();

        var error = await Assert.ThrowsAsync<AgentStartupBlockedException>(() => StartAsync(runtime, interactions));

        Assert.Contains("still waiting for a person", error.Message);
        Assert.Single(interactions.Opened);
        Assert.Contains("terminal.rm", _core.Methods);
    }

    [Fact]
    public async Task Without_a_person_the_same_blocked_startup_still_fails_and_reports_nothing()
    {
        _core.StatusAtLaunch = "blocked";
        _core.StatusKindAtLaunch = "permission";
        await using var runtime = NewRuntime(TimeSpan.FromSeconds(30));

        var error = await Assert.ThrowsAsync<AgentStartupBlockedException>(() => StartAsync(runtime, interactions: null));

        Assert.Contains("never answers dialogs", error.Message);
    }

    [Fact]
    public async Task An_agent_that_exits_while_waiting_for_a_person_fails_the_start()
    {
        _core.StatusAtLaunch = "blocked";
        _core.StatusKindAtLaunch = "permission";
        await using var runtime = NewRuntime(TimeSpan.FromSeconds(30));
        var interactions = new RecordingStartupInteractions();

        var start = StartAsync(runtime, interactions);
        await WaitUntilAsync(() => interactions.Opened.Count == 1);

        Ours().End("exited", 1);

        var error = await Assert.ThrowsAsync<AgentStartupBlockedException>(() => start);
        Assert.Contains("exited during startup", error.Message);
    }

    private sealed class RecordingStartupInteractions : IAgentStartupInteractions
    {
        public List<AgentInteraction> Opened { get; } = [];

        public int Closed { get; private set; }

        public Task OpenedAsync(AgentInteraction interaction, CancellationToken cancellationToken)
        {
            lock (Opened)
                Opened.Add(interaction);

            return Task.CompletedTask;
        }

        public Task ClosedAsync(CancellationToken cancellationToken)
        {
            Closed++;

            return Task.CompletedTask;
        }
    }
}

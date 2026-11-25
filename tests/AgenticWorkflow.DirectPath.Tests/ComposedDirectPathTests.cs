using AgenticWorkflow.Application;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Events;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Verification;
using AgenticWorkflow.Execution.Local;
using AgenticWorkflow.Persistence.Sqlite;

namespace AgenticWorkflow.DirectPath.Tests;

/// <summary>
/// The real adapters wired together (SQLite store, Git state provider, local verification runner) with a scripted
/// stand-in for the agent that edits real files. Proves the composition works without Herdr, Claude or any model call.
/// </summary>
public sealed class ComposedDirectPathTests : IDisposable
{
    private readonly DirectFixtureRepo _repo = DirectFixtureRepo.Create(
        ("src/Meals/Menu.cs", "public string GetMeal() => \"pasta\";\n"),
        ("src/Meals/Waiter.cs", "public string Serve(Menu m) => m.GetMeal();\n"),
        ("tests/Meals.Tests/MenuTests.cs", "Assert.Equal(\"pasta\", new Menu().GetMeal());\n"),
        ("src/Meals/Meals.csproj", "<Project />\n"));

    private readonly string _database = Path.Combine(Path.GetTempPath(), "aw-composed-" + Guid.NewGuid().ToString("N")[..8] + ".db");

    public void Dispose()
    {
        _repo.Dispose();

        foreach (var file in new[] { _database, _database + "-wal", _database + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    private void RenameEverywhere()
    {
        foreach (var path in new[] { "src/Meals/Menu.cs", "src/Meals/Waiter.cs", "tests/Meals.Tests/MenuTests.cs" })
            _repo.Write(path, _repo.Read(path).Replace("GetMeal", "GetFood"));
    }

    private async Task<(DirectRunResult Result, WorkflowTaskService Service, ScriptedRuntime Runtime)> RunAsync(params Action[] turns)
    {
        var service = await DirectPathSetup.OpenServiceAsync(_database);
        await DirectPathSetup.CreateTaskAsync(service, "Rename GetMeal to GetFood");

        var runtime = new ScriptedRuntime(turns);
        var runner = new DirectPathRunner(service, runtime, new GitImplementationStateProvider(), new LocalVerificationRunner());

        var result = await runner.RunAsync(DirectPathSetup.TaskId, DirectPathSetup.Definition(_repo.Root, DirectPathSetup.SymbolChecks()));
        return (result, service, runtime);
    }

    [Fact]
    public async Task A_real_rename_is_verified_by_the_controller_and_completed_and_survives_reopening_the_database()
    {
        var (result, _, runtime) = await RunAsync(RenameEverywhere);

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Equal(1, result.TurnsUsed);
        Assert.Equal(1, runtime.Stops);

        // a brand-new service over the same file reconstructs the same completed history
        var reopened = await DirectPathSetup.OpenServiceAsync(_database);
        var task = (await reopened.LoadAsync(DirectPathSetup.TaskId)).Task;

        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        var verification = task.CurrentVerification!;
        Assert.Equal(VerificationOutcome.Passed, verification.Outcome);
        Assert.Equal(task.CurrentImplementationRevisionId, verification.ImplementationRevisionId);
        Assert.StartsWith("git-worktree-sha256:", task.CurrentImplementationRevision!.Fingerprint.Value);
        Assert.Equal(
            [DirectPathSetup.OldSymbolAbsent.Value, DirectPathSetup.NewSymbolPresent.Value],
            verification.Checks.Select(c => c.CheckId.Value).ToArray());
    }

    [Fact]
    public async Task A_done_agent_that_changed_nothing_is_not_completed_and_gets_one_repair_message_then_succeeds()
    {
        var (result, service, runtime) = await RunAsync(() => { }, RenameEverywhere);

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Equal(2, result.TurnsUsed);
        Assert.Equal(2, runtime.Prompts.Count);
        Assert.Contains("old_symbol_absent", runtime.Prompts[1]);
        Assert.Contains("new_symbol_present", runtime.Prompts[1]); // both fail before the rename; passing checks are never sent back

        var task = (await service.LoadAsync(DirectPathSetup.TaskId)).Task;
        Assert.Equal(2, task.Verifications.Length);
        Assert.Equal(VerificationOutcome.Failed, task.Verifications[0].Outcome);
        Assert.Equal(VerificationOutcome.Passed, task.Verifications[1].Outcome);
        Assert.NotEqual(task.Verifications[0].ImplementationRevisionId, task.Verifications[1].ImplementationRevisionId);
    }

    [Fact]
    public async Task Changing_a_protected_file_blocks_completion_even_though_the_symbol_checks_pass()
    {
        var (result, service, _) = await RunAsync(() =>
        {
            RenameEverywhere();
            _repo.Write("src/Meals/Meals.csproj", "<Project><!-- tampered --></Project>\n");
        });

        Assert.Equal(DirectRunStatus.VerificationUnresolved, result.Status);

        var task = (await service.LoadAsync(DirectPathSetup.TaskId)).Task;
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.Contains(task.CurrentVerification!.Checks, c => c.CheckId == DirectPathRunner.ScopeCheckId && c.Outcome == VerificationOutcome.Failed);
    }

    [Fact]
    public async Task The_runner_changes_nothing_in_the_repository_itself()
    {
        var before = _repo.Git("status", "--porcelain=v1", "-z");
        var headBefore = _repo.Git("rev-parse", "HEAD");

        await RunAsync(RenameEverywhere);

        // only the "agent's" edits are present; no commit, no staging, no stash, no branch
        Assert.Equal(headBefore, _repo.Git("rev-parse", "HEAD"));
        Assert.Equal(string.Empty, _repo.Git("diff", "--cached", "--name-only"));
        Assert.Equal(string.Empty, _repo.Git("stash", "list"));
        Assert.Equal("main", _repo.Git("branch", "--show-current").Trim());
        Assert.NotEqual(before, _repo.Git("status", "--porcelain=v1", "-z"));
    }

    private sealed class ScriptedRuntime(IReadOnlyList<Action> turns) : IAgentRuntime
    {
        private int _turn;

        public List<string> Prompts { get; } = [];

        public int Stops { get; private set; }

        public Task<AgentSessionId> StartSessionAsync(AgentSessionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentSessionId("scripted"));

        public Task<AgentTurnNumber> SendTurnAsync(AgentSessionId session, string prompt, CancellationToken cancellationToken = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(new AgentTurnNumber(++_turn));
        }

        public Task<AgentTurnOutcome> WaitForTurnAsync(AgentSessionId session, AgentTurnNumber turn, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (turn.Value <= turns.Count)
                turns[turn.Value - 1]();

            return Task.FromResult(AgentTurnOutcome.Done);
        }

        public Task StopSessionAsync(AgentSessionId session, CancellationToken cancellationToken = default)
        {
            Stops++;
            return Task.CompletedTask;
        }
    }
}

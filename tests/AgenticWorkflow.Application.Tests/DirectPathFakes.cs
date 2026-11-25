using System.Security.Cryptography;
using System.Text;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.Tests;

/// <summary>
/// One scripted agent turn: what the "agent" does to the workspace, how the turn ends, and an optional hook while it is working.
/// <see cref="Observed"/> are the observations (awaiting human, resumed) the runtime reports, in order, before the turn's own outcome.
/// </summary>
internal sealed record TurnScript(
    Action? Edit = null,
    AgentTurnOutcome? Outcome = null,
    Func<Task>? WhileWorking = null,
    IReadOnlyList<AgentTurnOutcome>? Observed = null);

/// <summary>A deterministic in-memory agent runtime that records everything the runner does to it.</summary>
internal sealed class FakeAgentRuntime : IAgentRuntime
{
    private readonly Queue<TurnScript> _script = new();
    private int _nextSession;
    private readonly Dictionary<AgentSessionId, int> _turns = [];
    private int _waits;

    public List<AgentSessionRequest> Starts { get; } = [];

    public List<(AgentSessionId Session, string Prompt)> Sends { get; } = [];

    public List<AgentSessionId> Stops { get; } = [];

    /// <summary>Sessions that exist but belong to someone else; the runner must never touch them.</summary>
    public List<AgentSessionId> Unrelated { get; } = [new AgentSessionId("someone-elses-session")];

    public Exception? FailStart { get; set; }

    public Exception? FailStop { get; set; }

    public Exception? FailWait { get; set; }

    /// <summary>Thrown by <see cref="SendTurnAsync"/> after the send was recorded: the prompt may or may not have reached the agent, and the caller cannot tell.</summary>
    public Exception? FailSend { get; set; }

    /// <summary>Runs at the start of every wait, with the 0-based index of that wait within the turn (to observe what was durable at that moment).</summary>
    public Func<int, Task>? OnWait { get; set; }

    /// <summary>Runs while the session is being stopped (the stop has been requested and has not yet completed).</summary>
    public Func<Task>? OnStop { get; set; }

    public void Enqueue(params TurnScript[] turns)
    {
        foreach (var turn in turns)
            _script.Enqueue(turn);
    }

    public Task<AgentSessionId> StartSessionAsync(AgentSessionRequest request, CancellationToken cancellationToken = default)
    {
        Starts.Add(request);

        if (FailStart is { } failure)
            throw failure;

        var id = new AgentSessionId($"session-{++_nextSession}");
        _turns[id] = 0;
        return Task.FromResult(id);
    }

    /// <summary>A session that exists without being a recorded start: the new process of a resumed conversation.</summary>
    public AgentSessionId Attach()
    {
        var id = new AgentSessionId($"session-{++_nextSession}");
        _turns[id] = 0;
        return id;
    }

    private TurnScript? _current;

    public Task<AgentTurnNumber> SendTurnAsync(AgentSessionId session, string prompt, CancellationToken cancellationToken = default)
    {
        Assert.True(_turns.ContainsKey(session), "Unknown session.");
        Sends.Add((session, prompt));

        if (FailSend is { } failure)
            throw failure;

        _turns[session]++;
        _current = _script.Count > 0 ? _script.Dequeue() : new TurnScript();
        _waits = 0;
        return Task.FromResult(new AgentTurnNumber(_turns[session]));
    }

    public async Task<AgentTurnOutcome> WaitForTurnAsync(
        AgentSessionId session,
        AgentTurnNumber turn,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (FailWait is { } failure)
            throw failure;

        var script = _current ?? new TurnScript();
        var index = _waits++;

        if (OnWait is { } onWait)
            await onWait(index);

        // a cancelled wait throws, exactly as a real runtime's wait does
        cancellationToken.ThrowIfCancellationRequested();

        // A turn first reports each observation in order (the agent waited for a person, then resumed); the turn itself goes on afterwards.
        if (script.Observed is { } observed && index < observed.Count)
            return observed[index];

        // The agent "works": it edits the workspace and, in some tests, something else happens meanwhile.
        script.Edit?.Invoke();

        if (script.WhileWorking is { } hook)
            await hook();

        cancellationToken.ThrowIfCancellationRequested();
        return script.Outcome ?? AgentTurnOutcome.Done;
    }

    public async Task StopSessionAsync(AgentSessionId session, CancellationToken cancellationToken = default)
    {
        Stops.Add(session);

        if (OnStop is { } onStop)
            await onStop();

        if (FailStop is { } failure)
            throw failure;
    }
}

/// <summary>An in-memory "repository": files, a committed baseline, a fingerprint over the differences. Read-only to the runner.</summary>
internal sealed class FakeWorkspace : IImplementationStateProvider
{
    private readonly Dictionary<string, string> _committed = new()
    {
        ["src/Greeting.cs"] = "public string GetMeal() => \"hello\";",
        ["tests/GreetingTests.cs"] = "Assert.Equal(\"hello\", g.GetMeal());",
        ["Fixture.sln"] = "sln",
    };

    public Dictionary<string, string> Files { get; }

    public string Head { get; set; } = "HEAD-1";

    public int Captures { get; private set; }

    public FakeWorkspace()
    {
        Files = new Dictionary<string, string>(_committed);
    }

    public Task<ImplementationState> CaptureAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        Captures++;

        var digests = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Files.Keys.Union(_committed.Keys))
        {
            Files.TryGetValue(path, out var now);
            _committed.TryGetValue(path, out var before);

            if (now != before)
                digests[path] = now is null ? "deleted" : Hash(now);
        }

        var canonical = $"{Head}\n" + string.Join("\n", digests.Select(d => $"{d.Key}\0{d.Value}"));
        return Task.FromResult(new ImplementationState(
            new ImplementationFingerprint(Hash(canonical)),
            Head,
            new Dictionary<string, string>(digests)));
    }

    /// <summary>The renamed state: both files use GetFood.</summary>
    public void RenameToGetFood()
    {
        Files["src/Greeting.cs"] = Files["src/Greeting.cs"].Replace("GetMeal", "GetFood");
        Files["tests/GreetingTests.cs"] = Files["tests/GreetingTests.cs"].Replace("GetMeal", "GetFood");
    }

    public bool Renamed => !Files["src/Greeting.cs"].Contains("GetMeal") && Files["src/Greeting.cs"].Contains("GetFood");

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>Scripted check execution; by default the "tests" check passes iff the rename is complete.</summary>
internal sealed class FakeVerifier(FakeWorkspace workspace) : IVerificationRunner
{
    public List<string> Ran { get; } = [];

    public Func<VerificationCheckSpec, CheckExecution>? Override { get; set; }

    public Task<CheckExecution> RunAsync(string workspacePath, VerificationCheckSpec check, CancellationToken cancellationToken = default)
    {
        Ran.Add(check.Id.Value);

        if (Override is { } custom)
            return Task.FromResult(custom(check));

        return Task.FromResult(workspace.Renamed
            ? new CheckExecution(0, false, TimeSpan.FromMilliseconds(5), "Passed!", string.Empty)
            : new CheckExecution(1, false, TimeSpan.FromMilliseconds(5), "GreetingTests.ExpectedGreeting failed.", string.Empty));
    }
}

/// <summary>Everything wired together over the in-memory store.</summary>
internal sealed class DirectHarness
{
    public const string ProfileName = "claude-local";

    public static readonly VerificationCheckId TestsCheck = new("tests");

    public FakeTaskEventStore Store { get; } = new();

    public FakeAgentRuntime Runtime { get; } = new();

    public FakeWorkspace Workspace { get; } = new();

    public FakeVerifier Verifier { get; }

    public WorkflowTaskService Service { get; private set; } = null!;

    public TaskSnapshot Snapshot { get; set; } = null!;

    private DirectHarness()
    {
        Verifier = new FakeVerifier(Workspace);
    }

    /// <param name="acceptance">
    /// The task's acceptance requirement. Defaults to <see cref="AcceptanceRequirement.HumanRequired"/>, the semantics the M4A-era tests here were written
    /// for (verified work opens the gate). The tests of the no-acceptance path say <see cref="AcceptanceRequirement.NotRequired"/> explicitly
    /// (<c>DirectAcceptancePolicyTests</c>).
    /// </param>
    public static async Task<DirectHarness> CreateAsync(
        CompletionBoundary? boundary = null,
        bool bindRoot = true,
        string boundProfile = ProfileName,
        AcceptanceRequirement acceptance = AcceptanceRequirement.HumanRequired,
        bool whatRequired = false,
        bool howRequired = false,
        bool reviewRequired = false,
        string? reviewerProfile = null,
        bool proveRequired = false,
        string? proverProfile = null)
    {
        var harness = new DirectHarness();
        harness.Service = new WorkflowTaskService(harness.Store, new SequentialIdSource(), new SteppingClock(Fixture.T0));

        var created = await harness.Service.CreateAsync(
            Fixture.TaskOne, "Rename GetMeal() to GetFood()", boundary ?? CompletionBoundary.WorkingTree, acceptance, whatRequired: whatRequired, howRequired: howRequired, reviewRequired: reviewRequired, proveRequired: proveRequired);
        harness.Snapshot = created.Snapshot;

        if (bindRoot)
        {
            var bound = await harness.Service.BindRoleAsync(
                harness.Snapshot,
                DirectPathRoles.Root,
                Fixture.Profile(boundProfile, "code_edit", "repository_tools", "fresh_context"));
            harness.Snapshot = bound.Snapshot;
        }

        // an explicit reviewer binding (a profile of its own unless the test names the implementer's), or none at all.
        if (reviewerProfile is not null)
        {
            var bound = await harness.Service.BindRoleAsync(harness.Snapshot, DirectPathRoles.Reviewer, Fixture.Profile(reviewerProfile, "repository_tools"));
            harness.Snapshot = bound.Snapshot;
        }

        // an explicit prover binding, or none at all.
        if (proverProfile is not null)
        {
            var bound = await harness.Service.BindRoleAsync(harness.Snapshot, DirectPathRoles.Prover, Fixture.Profile(proverProfile, "repository_tools"));
            harness.Snapshot = bound.Snapshot;
        }

        return harness;
    }

    public DirectTaskDefinition Definition(int maxTurns = 3, WorkspaceScope? scope = null, string profile = ProfileName)
        => new(
            "Rename the public method GetMeal() to GetFood() and update all references.",
            @"C:\fixture",
            DirectPathRoles.RootRoleId,
            new ProfileId(profile),
            scope ?? new WorkspaceScope(["src/", "tests/"], ["Fixture.sln"]),
            [new VerificationCheckSpec(TestsCheck, "dotnet test passes", "dotnet", ["test"], TimeSpan.FromMinutes(2))],
            maxTurns);

    /// <summary>The optional native-interaction observer the runner is given (null: none).</summary>
    public INativeInteractionObserver? Observer { get; set; }

    public DirectPathRunner Runner(DirectRunOptions? options = null)
        => new(Service, Runtime, Workspace, Verifier, options, interactions: Observer);

    public Task<DirectRunResult> RunAsync(DirectTaskDefinition? definition = null, DirectRunOptions? options = null, CancellationToken ct = default)
        => Runner(options).RunAsync(Fixture.TaskOne, definition ?? Definition(), ct);

    public async Task<WorkflowTask> ReloadAsync() => (await Service.LoadAsync(Fixture.TaskOne)).Task;

    /// <summary>Turn script: the agent performs the rename, then reports done.</summary>
    public TurnScript Renames() => new(Workspace.RenameToGetFood);

    /// <summary>Turn script: the agent reports done without changing anything.</summary>
    public static TurnScript DoesNothing() => new();
}

using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Evidence;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.Tests;

/// <summary>Builders for provider-neutral observed activity. Nothing here knows any harness.</summary>
internal static class Obs
{
    public const string Workspace = @"C:\fixture";

    public const string BuildCommand = "dotnet build Fixture.sln --no-incremental";

    public const string TestCommand = "dotnet test Fixture.sln";

    public static ObservedActivity Command(
        string id,
        int batch,
        string command,
        ImplementationFingerprint? state,
        string? directory = Workspace,
        bool succeeded = true,
        int batchSize = 1,
        long? milliseconds = 1500,
        bool exitUnproven = false)
        => new()
        {
            ToolUseId = id,
            Batch = batch,
            BatchSize = batchSize,
            Kind = ObservedActivityKind.Command,
            Command = command,
            WorkingDirectory = directory,
            Succeeded = succeeded,
            ExitUnproven = exitUnproven,
            DurationMilliseconds = milliseconds,
            StateAfterBatch = state,
        };

    public static ObservedActivity Edit(string id, int batch, int batchSize = 1)
        => new() { ToolUseId = id, Batch = batch, BatchSize = batchSize, Kind = ObservedActivityKind.WorkspaceMutation };

    public static ObservedActivity Read(string id, int batch, int batchSize = 1)
        => new() { ToolUseId = id, Batch = batch, BatchSize = batchSize, Kind = ObservedActivityKind.ReadOnly };

    public static ObservedTurnEvidence Evidence(params ObservedActivity[] activities)
        => new("fake-harness", activities, []);

    public static ObservedTurnEvidence Untrusted(string problem)
        => new("fake-harness", [], [problem]);

    public static ImplementationFingerprint Fingerprint(string name) => new($"fingerprint:{name}");

    public static VerificationCheckSpec Build { get; } = new(
        new VerificationCheckId("build"),
        "the solution builds",
        "dotnet",
        ["build", "Fixture.sln", "--no-incremental"],
        TimeSpan.FromMinutes(5));

    public static VerificationCheckSpec Tests { get; } = new(
        new VerificationCheckId("tests"),
        "the whole test suite passes",
        "dotnet",
        ["test", "Fixture.sln"],
        TimeSpan.FromMinutes(5));

    /// <summary>A check the agent is never asked to run: only the controller executes it.</summary>
    public static VerificationCheckSpec ControllerOnly { get; } = new(
        new VerificationCheckId("controller_only"),
        "GetFood is present",
        "git",
        ["grep", "-q", "GetFood"],
        TimeSpan.FromSeconds(30));
}

/// <summary>A scripted evidence source: one factory per turn, called when the runner collects. Records every call in order.</summary>
internal sealed class ScriptedEvidenceSource : IExecutionEvidenceSource
{
    private readonly Queue<Func<Task<ObservedTurnEvidence?>>> _turns = new();

    public List<string> Calls { get; } = [];

    public Exception? FailBegin { get; set; }

    public Exception? FailCollect { get; set; }

    public void Enqueue(Func<Task<ObservedTurnEvidence?>> turn) => _turns.Enqueue(turn);

    public void Enqueue(ObservedTurnEvidence? evidence) => _turns.Enqueue(() => Task.FromResult(evidence));

    public Task BeginTurnAsync(AgentSessionId session, CancellationToken cancellationToken = default)
    {
        Calls.Add("begin");

        return FailBegin is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }

    public Task<ObservedTurnEvidence?> CollectTurnAsync(AgentSessionId session, CancellationToken cancellationToken = default)
    {
        Calls.Add("collect");

        if (FailCollect is { } failure)
        {
            return Task.FromException<ObservedTurnEvidence?>(failure);
        }

        return _turns.Count > 0 ? _turns.Dequeue()() : Task.FromResult<ObservedTurnEvidence?>(null);
    }
}

/// <summary>Counts what the controller's own runner was asked to execute.</summary>
internal static class HarnessEvidenceExtensions
{
    public static DirectTaskDefinition EvidenceDefinition(this DirectHarness harness, int maxTurns = 3)
        => new(
            "Rename the public method GetMeal() to GetFood() and update all references.",
            Obs.Workspace,
            DirectPathRoles.RootRoleId,
            new ProfileId(DirectHarness.ProfileName),
            new WorkspaceScope(["src/", "tests/"], ["Fixture.sln"]),
            [Obs.Build, Obs.Tests, Obs.ControllerOnly],
            maxTurns);

    public static DirectPathRunner RunnerWith(this DirectHarness harness, IExecutionEvidenceSource? evidence)
        => new(harness.Service, harness.Runtime, harness.Workspace, harness.Verifier, null, evidence);

    public static Task<DirectRunResult> RunWithAsync(this DirectHarness harness, IExecutionEvidenceSource? evidence, int maxTurns = 3)
        => harness.RunnerWith(evidence).RunAsync(Fixture.TaskOne, harness.EvidenceDefinition(maxTurns));

    public static async Task<ImplementationFingerprint> CurrentFingerprintAsync(this DirectHarness harness)
        => (await harness.Workspace.CaptureAsync(Obs.Workspace)).Fingerprint;
}

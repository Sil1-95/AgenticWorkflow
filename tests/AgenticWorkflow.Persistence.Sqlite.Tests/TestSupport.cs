global using AgenticWorkflow.Application;
global using AgenticWorkflow.Application.Persistence;
global using AgenticWorkflow.Core;
global using AgenticWorkflow.Core.Authority;
global using AgenticWorkflow.Core.Events;
global using AgenticWorkflow.Core.Gates;
global using AgenticWorkflow.Core.Identity;
global using AgenticWorkflow.Core.Lifecycle;
global using AgenticWorkflow.Core.Policy;
global using AgenticWorkflow.Core.Revisions;
global using AgenticWorkflow.Core.Roles;
global using AgenticWorkflow.Persistence.Sqlite;
global using AgenticWorkflow.Core.Verification;
using Microsoft.Data.Sqlite;

namespace AgenticWorkflow.Persistence.Sqlite.Tests;

/// <summary>Deterministic GUIDs 00000000-0000-0000-0000-000000000001, ...002, ...</summary>
internal sealed class SequentialIdSource : IIdSource
{
    private int _next;

    public Guid Next() => new(++_next, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// A database file in its own temporary directory, deleted (with its -wal/-shm companions) on dispose.
/// Nothing is ever created inside the repository.
/// </summary>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _directory;

    public TempDatabase()
    {
        _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AgenticWorkflow.Persistence.Sqlite.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, "events.db");
    }

    public string Path { get; }

    public Task<SqliteEventStore> OpenStoreAsync(TimeSpan? busyTimeout = null)
        => SqliteEventStore.OpenAsync(Path, busyTimeout ?? TimeSpan.FromSeconds(30));

    /// <summary>Runs raw SQL against the file, bypassing the store (used to inspect or deliberately damage it).</summary>
    public int Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);

        return command.ExecuteNonQuery();
    }

    public T Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);

        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Never fail a test over temp cleanup; a stuck handle would already have failed the test that caused it.
        }
    }

    private SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
        connection.Open();
        return connection;
    }
}

internal static class Fixture
{
    public static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static readonly TaskId TaskOne = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    public static readonly TaskId TaskTwo = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    public static DateTimeOffset At(int minutes) => T0.AddMinutes(minutes);

    /// <summary>
    /// A new task. Defaults to <see cref="AcceptanceRequirement.HumanRequired"/>: most persistence tests are about gates and plain state changes other than COMPLETED (a plain COMPLETED needs
    /// <see cref="AsLegacy"/>). Tests of the requirement itself pass it explicitly (<c>AcceptancePersistenceTests</c>).
    /// </summary>
    public static TaskStream NewTask(
        TaskId? id = null,
        IIdSource? ids = null,
        string intent = "Add retry to the importer",
        AcceptanceRequirement acceptance = AcceptanceRequirement.HumanRequired)
        => TaskStream.Create(id ?? TaskOne, intent, CompletionBoundary.LocalCommit, T0, ids ?? new GuidIdSource(), acceptance);

    /// <summary>
    /// The same history as <paramref name="stream"/>, marked as recorded before the acceptance requirement existed. The codec stores such a task_created
    /// exactly as the pre-M4B build did (payload version 1, no acceptance member), which is the only kind of history that may complete by a plain state change.
    /// </summary>
    public static TaskStream AsLegacy(TaskStream stream)
    {
        var first = stream.Events[0];
        var created = (TaskCreated)first.Payload with { PredatesAcceptanceRequirement = true };
        var events = stream.Events.Skip(1).Prepend(new EventEnvelope(first.Id, first.TaskId, first.Sequence, first.Timestamp, created));

        return TaskStream.Replay(events, new GuidIdSource());
    }

    public static Role Role(string id, params string[] required)
        => new(new RoleId(id), IdentifierSet<ProfileCapability>.Of(required.Select(c => new ProfileCapability(c))));

    public static Profile Profile(string id, string? description, params string[] capabilities)
        => new(new ProfileId(id), IdentifierSet<ProfileCapability>.Of(capabilities.Select(c => new ProfileCapability(c))), description);

    /// <summary>The events of <paramref name="stream"/> after the first <paramref name="knownVersion"/>.</summary>
    public static IReadOnlyList<EventEnvelope> NewEvents(TaskStream stream, long knownVersion)
        => stream.Events.Skip((int)knownVersion).ToList();

    public static EventEnvelope Retarget(EventEnvelope e, TaskId? task = null, long? sequence = null, EventId? id = null)
        => new(id ?? e.Id, task ?? e.TaskId, sequence ?? e.Sequence, e.Timestamp, e.Payload);

    /// <summary>
    /// Exercises every event type, every policy effect, optional and absent references, role/profile capabilities,
    /// authority sets, an implementation revision bound to an older and to a newer semantic revision, odd
    /// fingerprints and non-UTC offsets, and ends in a terminal state.
    /// </summary>
    public static TaskStream RichCompletedTask(TaskId? id = null)
    {
        var offset = TimeSpan.FromMinutes(330);
        DateTimeOffset Local(int minutes) => At(minutes).ToOffset(offset);

        var stream = TaskStream.Create(
            id ?? TaskOne,
            "Add retry to the importer \"quoted\" — with ünïcode and <angle> & 'apostrophes'",
            new CompletionBoundary("staging_deploy"),
            Local(0).AddTicks(1234567),
            new SequentialIdSource(),
            AcceptanceRequirement.HumanRequired);

        stream.BindRole(
            Role("implementer", "code_edit", "repository_tools"),
            Profile("p-impl", "Primary implementer", "code_edit", "repository_tools", "fresh_context"),
            Local(1));
        stream.BindRole(Role("observer"), Profile("p-obs", null), Local(1));
        stream.ChangeAuthority(
            new AuthorityEnvelope(
                IdentifierSet<ActionId>.Of(new ActionId("dependency_change"), new ActionId("public_api_change")),
                IdentifierSet<ActionId>.Of(new ActionId("deployment"))),
            "Initial delegation.",
            Local(2));

        stream.RecordImplementationRevision(new ImplementationFingerprint("  tree:ABC::123  "), Local(3));
        stream.CreateSemanticRevision(new SemanticChangeKind("compliance_ruling"), "Legal narrowed the scope.", Local(4));
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:def456"), At(5));

        stream.RecordVerification(
            stream.State.CurrentImplementationRevisionId!.Value,
            [
                new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Failed, "exit 1 (expected 0)", 1),
                new VerificationCheckResult(new VerificationCheckId("symbol_absent"), VerificationOutcome.Passed, "no matches — \"ok\""),
            ],
            Local(5));
        stream.RecordVerification(
            stream.State.CurrentImplementationRevisionId!.Value,
            [new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "exit 0", 0)],
            Local(5));

        stream.RecordPolicyDecision(
            new PolicyRuleId("deps.review"),
            PolicyEffect.Require,
            new PolicyTrigger("package manifest touched", new ActionId("dependency_change"), "evt-123"),
            "Dependency change needs human sign-off.",
            Local(6),
            stream.State.CurrentSemanticRevisionId,
            stream.State.CurrentImplementationRevisionId);
        stream.RecordPolicyDecision(new PolicyRuleId("secrets.guard"), PolicyEffect.Prevent, new PolicyTrigger("secret in diff"), "Blocked.", Local(7));
        stream.RecordPolicyDecision(new PolicyRuleId("style.note"), PolicyEffect.Detect, new PolicyTrigger("long method"), "Observed.", Local(8));
        stream.RecordPolicyDecision(new PolicyRuleId("ops.heads_up"), PolicyEffect.Notify, new PolicyTrigger("touches CI"), "Informed.", Local(9));

        stream.ChangeWorkflowState(WorkflowState.AwaitingHuman, "Waiting for sign-off.", Local(10));
        stream.ChangeWorkflowState(WorkflowState.Blocked, "Waiting on an external service.", Local(11));
        stream.ChangeWorkflowState(WorkflowState.Running, "Unblocked.", Local(12));

        // new evidence reclassifies the task to the HOW (M6, an additive escalation): the HOW is approved, then the work is implemented and verified again
        stream.Reclassify(new ReclassificationId(new Guid(77, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)), ReclassificationStage.How, "A \"hidden\" constraint — ünïcode & <angle>.", "The importer cannot keep its shape.", 2, "session-é", new ImplementationFingerprint("tree:edited"), true, Local(12));
        stream.OpenHowGate(new GateId(new Guid(78, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)), new HowProposal("Wrap the importer.", "A wrapper decides.", null, null, "One more class.", null, "Two shapes are plausible.", 3), Local(12));
        stream.ApproveHow(stream.State.PendingGate!.Id, Local(12));
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:wrapped"), Local(12));
        stream.RecordVerification(
            stream.State.CurrentImplementationRevisionId!.Value,
            [new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "exit 0", 0)],
            Local(12));

        // an independent REVIEW: the agent asks for one (additive), a fresh reviewer requires changes, the repair is verified again and a second fresh reviewer passes it
        var reviewer = new AgenticWorkflow.Core.Roles.ProfileId("p-reviewer");
        stream.BindRole(Role("reviewer", "repository_tools"), Profile("p-reviewer", "Independent reviewer", "repository_tools"), Local(12));
        stream.RequestReview(new AgenticWorkflow.Core.Identity.ReviewRequestId(new Guid(80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)), "A \"subtle\" change — ünïcode & <angle>.", 2, "session-é", Local(12));
        stream.RecordReview(
            new AgenticWorkflow.Core.Identity.ReviewId(new Guid(81, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            new AgenticWorkflow.Core.Reviews.ReviewReport(
                AgenticWorkflow.Core.Reviews.ReviewVerdict.ChangesRequired,
                "One \"edge\" is wrong — ünïcode.",
                [new AgenticWorkflow.Core.Reviews.ReviewFinding("Empty input", "Importer.cs:12 returns null", "Callers crash", "Return an empty list", "Importer.cs:12")],
                "A non-blocking <note>."),
            reviewer,
            "reviewer-session-1",
            "session-é",
            1234,
            2,
            ["git diff", "dotnet test --filter Importer"],
            configuredCheckRerun: false,
            Local(12));
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:repaired"), Local(12));
        stream.RecordVerification(
            stream.State.CurrentImplementationRevisionId!.Value,
            [new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "exit 0", 0)],
            Local(12));
        stream.RecordReview(
            new AgenticWorkflow.Core.Identity.ReviewId(new Guid(82, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            new AgenticWorkflow.Core.Reviews.ReviewReport(AgenticWorkflow.Core.Reviews.ReviewVerdict.Pass, "Looks right.", []),
            reviewer,
            "reviewer-session-2",
            "session-é",
            900,
            0,
            [],
            configuredCheckRerun: false,
            Local(12));

        // formal PROVE: the agent's Proof Contract escalates it (additive); an independent prover proves it over the verified, reviewed work
        var prover = new AgenticWorkflow.Core.Roles.ProfileId("p-prover");
        stream.BindRole(Role("prover", "repository_tools"), Profile("p-prover", "Independent prover", "repository_tools"), Local(12));
        stream.RecordProofContract(
            new AgenticWorkflow.Core.Identity.ProofContractId(new Guid(90, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            "The \"edge\" claims — ünïcode & <angle>.",
            [
                new AgenticWorkflow.Core.Proofs.ProofClaim(
                    "c1",
                    "An empty file yields an empty list — ünïcode.",
                    [AgenticWorkflow.Core.Proofs.ProofEvidenceRequirement.Verification("tests"), AgenticWorkflow.Core.Proofs.ProofEvidenceRequirement.CurrentReviewPass(), AgenticWorkflow.Core.Proofs.ProofEvidenceRequirement.Observation("run the importer on an empty file")],
                    "The importer returns [] and exits 0."),
            ],
            2,
            "session-é",
            Local(12));
        stream.RecordProof(
            new AgenticWorkflow.Core.Identity.ProofId(new Guid(91, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            new AgenticWorkflow.Core.Proofs.ProofReport(
                "Proven — ünïcode & <angle>.",
                [
                    new AgenticWorkflow.Core.Proofs.ProofClaimResult(
                        "c1",
                        AgenticWorkflow.Core.Proofs.ProofVerdict.Pass,
                        [
                            new AgenticWorkflow.Core.Proofs.ProofEvidenceRef(AgenticWorkflow.Core.Proofs.ProofEvidenceRefKind.Verification, "tests"),
                            new AgenticWorkflow.Core.Proofs.ProofEvidenceRef(AgenticWorkflow.Core.Proofs.ProofEvidenceRefKind.Review, new Guid(82, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0).ToString()),
                            new AgenticWorkflow.Core.Proofs.ProofEvidenceRef(AgenticWorkflow.Core.Proofs.ProofEvidenceRefKind.Command, "1"),
                        ],
                        "Ran it on an empty file."),
                ]),
            prover,
            "prover-session-1",
            "session-é",
            2100,
            1,
            ["dotnet run -- empty.csv"],
            configuredCheckRerun: false,
            0,
            Local(12));

        // the ACCEPT gate: opened, the human asks for changes (a new semantic revision), the work is verified again, a second gate is accepted
        stream.OpenAcceptGate(Local(13));
        stream.RequestChanges(stream.State.PendingGate!.Id, "Please also cover the \"empty\" case — ünïcode & <angle>.", Local(14));
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:ghi789"), Local(15));
        stream.RecordVerification(
            stream.State.CurrentImplementationRevisionId!.Value,
            [new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "exit 0", 0)],
            Local(16));
        stream.RecordReview(
            new AgenticWorkflow.Core.Identity.ReviewId(new Guid(83, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            new AgenticWorkflow.Core.Reviews.ReviewReport(AgenticWorkflow.Core.Reviews.ReviewVerdict.Pass, "Still right after the human's change.", []),
            reviewer,
            "reviewer-session-3",
            "session-é",
            700,
            1,
            ["git diff"],
            configuredCheckRerun: false,
            Local(16));
        // the human's change is a new semantic revision: the contract is stale, so a new one is submitted and proven over the re-verified, re-reviewed work
        stream.RecordProofContract(
            new AgenticWorkflow.Core.Identity.ProofContractId(new Guid(92, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            "The claims after the human's change.",
            [new AgenticWorkflow.Core.Proofs.ProofClaim("c1", "The empty case is covered.", [AgenticWorkflow.Core.Proofs.ProofEvidenceRequirement.Verification("tests")], "The test passes.")],
            3,
            "session-é",
            Local(16));
        stream.RecordProof(
            new AgenticWorkflow.Core.Identity.ProofId(new Guid(93, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            new AgenticWorkflow.Core.Proofs.ProofReport(
                "Proven again.",
                [new AgenticWorkflow.Core.Proofs.ProofClaimResult("c1", AgenticWorkflow.Core.Proofs.ProofVerdict.Pass, [new AgenticWorkflow.Core.Proofs.ProofEvidenceRef(AgenticWorkflow.Core.Proofs.ProofEvidenceRefKind.Verification, "tests")], "The test passes.")]),
            prover,
            "prover-session-2",
            "session-é",
            1500,
            0,
            [],
            configuredCheckRerun: true,
            300,
            Local(16));
        stream.OpenAcceptGate(Local(17));
        stream.AcceptGate(stream.State.PendingGate!.Id, Local(18));
        return stream;
    }

    public static async Task PersistAsync(SqliteEventStore store, TaskStream stream, long knownVersion = 0)
        => await store.AppendAsync(stream.State.Id, knownVersion, NewEvents(stream, knownVersion));

    public static async Task<TaskStream> LoadAndReplayAsync(SqliteEventStore store, TaskId id)
        => TaskStream.Replay(await store.LoadAsync(id), new GuidIdSource());
}

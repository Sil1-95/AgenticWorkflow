global using AgenticWorkflow.Application;
global using AgenticWorkflow.Application.Persistence;
global using AgenticWorkflow.Core;
global using AgenticWorkflow.Core.Authority;
global using AgenticWorkflow.Core.Events;
global using AgenticWorkflow.Core.Identity;
global using AgenticWorkflow.Core.Lifecycle;
global using AgenticWorkflow.Core.Policy;
global using AgenticWorkflow.Core.Revisions;
global using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Application.Tests;

/// <summary>Deterministic GUIDs 00000000-0000-0000-0000-000000000001, ...002, ...</summary>
internal sealed class SequentialIdSource : IIdSource
{
    private int _next;

    public Guid Next() => new(++_next, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>A clock that moves one minute on every read, so each command gets its own predictable timestamp.</summary>
internal sealed class SteppingClock : IClock
{
    private DateTimeOffset _now;

    public SteppingClock(DateTimeOffset start) => _now = start;

    public DateTimeOffset UtcNow
    {
        get
        {
            var current = _now;
            _now = _now.AddMinutes(1);
            return current;
        }
    }
}

/// <summary>
/// A deterministic in-memory <see cref="ITaskEventStore"/> with the same optimistic-concurrency contract as a real
/// one, plus instrumentation: every append attempt is counted and every accepted batch is recorded, and tests can
/// inject a competing writer or a failure between a load and an append.
/// </summary>
internal sealed class FakeTaskEventStore : ITaskEventStore
{
    private readonly Dictionary<TaskId, List<EventEnvelope>> _streams = [];

    public int LoadCalls { get; private set; }

    /// <summary>Every append call, including ones that were refused or failed.</summary>
    public int AppendCalls { get; private set; }

    /// <summary>Only batches that were durably accepted.</summary>
    public List<IReadOnlyList<EventEnvelope>> AcceptedBatches { get; } = [];

    /// <summary>Runs inside the next append call before it is checked (then clears itself). Simulates another writer.</summary>
    public Action? BeforeNextAppend { get; set; }

    /// <summary>Thrown by the next append call before anything is stored (then clears itself).</summary>
    public Exception? FailNextAppend { get; set; }

    /// <summary>When set, the next load returns this history instead of the stored one (then clears itself).</summary>
    public IReadOnlyList<EventEnvelope>? NextLoadResult { get; set; }

    /// <summary>When set, the next successful append reports this version (then clears itself).</summary>
    public long? NextReportedVersion { get; set; }

    public IReadOnlyList<EventEnvelope> History(TaskId taskId)
        => _streams.TryGetValue(taskId, out var events) ? events.ToList() : [];

    public Task<IReadOnlyList<EventEnvelope>> LoadAsync(TaskId taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LoadCalls++;

        if (NextLoadResult is { } forced)
        {
            NextLoadResult = null;
            return Task.FromResult(forced);
        }

        return Task.FromResult(History(taskId));
    }

    public Task<long> AppendAsync(
        TaskId taskId,
        long expectedVersion,
        IReadOnlyList<EventEnvelope> events,
        CancellationToken cancellationToken = default)
    {
        AppendCalls++;

        if (BeforeNextAppend is { } hook)
        {
            BeforeNextAppend = null;
            hook();
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (FailNextAppend is { } failure)
        {
            FailNextAppend = null;
            throw failure;
        }

        var stream = _streams.TryGetValue(taskId, out var existing) ? existing : null;
        var actualVersion = stream?.Count ?? 0;
        if (actualVersion != expectedVersion)
            throw new StreamVersionConflictException(taskId, expectedVersion, actualVersion);

        if (events.Count == 0)
            throw new ArgumentException("There is nothing to append.", nameof(events));

        if (stream is null)
        {
            stream = [];
            _streams[taskId] = stream;
        }

        stream.AddRange(events);
        AcceptedBatches.Add(events.ToList());

        var reported = NextReportedVersion ?? stream.Count;
        NextReportedVersion = null;
        return Task.FromResult(reported);
    }

    /// <summary>Appends directly, as if another process had written (bypassing instrumentation counters).</summary>
    public void Seed(TaskId taskId, IReadOnlyList<EventEnvelope> events)
    {
        if (!_streams.TryGetValue(taskId, out var stream))
        {
            stream = [];
            _streams[taskId] = stream;
        }

        stream.AddRange(events);
    }
}

internal static class Fixture
{
    public static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static readonly TaskId TaskOne = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    public static readonly TaskId TaskTwo = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    public static WorkflowTaskService NewService(ITaskEventStore store, IIdSource? ids = null, IClock? clock = null)
        => new(store, ids ?? new SequentialIdSource(), clock ?? new SteppingClock(T0));

    public static Task<TaskChange> CreateAsync(WorkflowTaskService service, TaskId? id = null)
        => service.CreateAsync(id ?? TaskOne, "Add retry to the importer", CompletionBoundary.LocalCommit);

    public static Role Role(string id, params string[] required)
        => new(new RoleId(id), IdentifierSet<ProfileCapability>.Of(required.Select(c => new ProfileCapability(c))));

    public static Profile Profile(string id, params string[] capabilities)
        => new(new ProfileId(id), IdentifierSet<ProfileCapability>.Of(capabilities.Select(c => new ProfileCapability(c))));

    public static AuthorityEnvelope Envelope(string[] allowed, string[] reserved)
        => new(
            IdentifierSet<ActionId>.Of(allowed.Select(a => new ActionId(a))),
            IdentifierSet<ActionId>.Of(reserved.Select(a => new ActionId(a))));

    public static PolicyTrigger Trigger() => new("package manifest touched");

    /// <summary>The same sequence of application calls every time; returns the final change.</summary>
    public static async Task<TaskChange> RunStandardSequenceAsync(WorkflowTaskService service)
    {
        var change = await CreateAsync(service);
        change = await service.BindRoleAsync(change.Snapshot, Role("implementer", "code_edit"), Profile("p-impl", "code_edit"));
        change = await service.ChangeAuthorityEnvelopeAsync(change.Snapshot, Envelope(["dependency_change"], ["deployment"]), "initial delegation");
        change = await service.CreateSemanticRevisionAsync(change.Snapshot, SemanticChangeKind.RequirementChange, "Also retry on 429.");
        change = await service.RecordImplementationRevisionAsync(change.Snapshot, new ImplementationFingerprint("tree:abc123"));
        change = await service.RecordPolicyDecisionAsync(
            change.Snapshot,
            new PolicyRuleId("deps.review"),
            PolicyEffect.Require,
            Trigger(),
            "Needs sign-off.",
            change.Task.CurrentSemanticRevisionId,
            change.Task.CurrentImplementationRevisionId);
        return await service.ChangeWorkflowStateAsync(change.Snapshot, WorkflowState.AwaitingHuman, "Waiting for sign-off.");
    }
}

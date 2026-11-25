global using AgenticWorkflow.Core;
global using AgenticWorkflow.Core.Authority;
global using AgenticWorkflow.Core.Events;
global using AgenticWorkflow.Core.Gates;
global using AgenticWorkflow.Core.Identity;
global using AgenticWorkflow.Core.Lifecycle;
global using AgenticWorkflow.Core.Policy;
global using AgenticWorkflow.Core.Revisions;
global using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Core.Tests;

/// <summary>Deterministic GUIDs 00000000-0000-0000-0000-000000000001, ...002, ...</summary>
internal sealed class SequentialIdSource : IIdSource
{
    private int _next;

    public Guid Next() => new(++_next, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

internal static class Fixture
{
    public static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset At(int minutes) => T0.AddMinutes(minutes);

    public static readonly TaskId TaskOne = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    public static readonly TaskId TaskTwo = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    /// <summary>
    /// A new task. Most Core tests exercise structure (revisions, replay, gates) rather than the completion policy, so they default to
    /// <see cref="AcceptanceRequirement.HumanRequired"/>, which opens gates (a plain COMPLETED is refused for it; tests that need one use <see cref="AsLegacy"/>). Tests about the no-acceptance path say <see cref="AcceptanceRequirement.NotRequired"/> explicitly (see <c>AcceptanceRequirementTests</c>).
    /// </summary>
    public static TaskStream NewTask(IIdSource? ids = null, TaskId? id = null, AcceptanceRequirement acceptance = AcceptanceRequirement.HumanRequired)
        => TaskStream.Create(id ?? TaskOne, "Add retry to the importer", CompletionBoundary.LocalCommit, T0, ids ?? new SequentialIdSource(), acceptance);

    /// <summary>
    /// The same history as <paramref name="stream"/>, marked as recorded before the acceptance requirement existed (what a stored task_created version 1
    /// decodes to). Only such a history may still complete by a plain recorded COMPLETED; use it for tests about structure, not about acceptance.
    /// </summary>
    public static TaskStream AsLegacy(TaskStream stream)
    {
        var first = stream.Events[0];
        var created = (TaskCreated)first.Payload with { PredatesAcceptanceRequirement = true };
        var events = stream.Events.Skip(1).Prepend(new EventEnvelope(first.Id, first.TaskId, first.Sequence, first.Timestamp, created));

        return TaskStream.Replay(events, new GuidIdSource());   // fresh ids: a sequential source would repeat the ids already in the history
    }

    public static TaskStream NewLegacyTask(IIdSource? ids = null, TaskId? id = null) => AsLegacy(NewTask(ids, id));

    public static ProfileCapability Cap(string value) => new(value);

    public static Role Role(string id, params string[] required)
        => new(new RoleId(id), IdentifierSet<ProfileCapability>.Of(required.Select(Cap)));

    public static Profile Profile(string id, params string[] capabilities)
        => new(new ProfileId(id), IdentifierSet<ProfileCapability>.Of(capabilities.Select(Cap)));

    public static AuthorityEnvelope Envelope(string[] allowed, string[] reserved)
        => new(
            IdentifierSet<ActionId>.Of(allowed.Select(a => new ActionId(a))),
            IdentifierSet<ActionId>.Of(reserved.Select(a => new ActionId(a))));

    public static PolicyTrigger Trigger(string description = "package manifest touched")
        => new(description);

    public static EventEnvelope Retarget(EventEnvelope e, TaskId? task = null, long? sequence = null, DateTimeOffset? at = null, EventId? id = null)
        => new(id ?? e.Id, task ?? e.TaskId, sequence ?? e.Sequence, at ?? e.Timestamp, e.Payload);

    /// <summary>new task -> role binding -> authority change -> semantic revision -> implementation revision -> state change.</summary>
    public static TaskStream RichHistory(IIdSource? ids = null)
    {
        var stream = NewTask(ids);
        stream.BindRole(Role("implementer", "code_edit"), Profile("p-impl", "code_edit", "repository_tools"), At(1));
        stream.ChangeAuthority(Envelope(["dependency_change"], ["deployment"]), "initial delegation", At(2));
        stream.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "Importer must also retry on 429.", At(3));
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:abc123"), At(4));
        stream.RecordPolicyDecision(
            new PolicyRuleId("deps.review"),
            PolicyEffect.Require,
            Trigger(),
            "Dependency change needs human sign-off.",
            At(5),
            stream.State.CurrentSemanticRevisionId,
            stream.State.CurrentImplementationRevisionId);
        stream.ChangeWorkflowState(WorkflowState.AwaitingHuman, "Waiting for dependency sign-off.", At(6));
        return stream;
    }
}

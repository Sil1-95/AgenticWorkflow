using System.Collections.Immutable;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Core.Revisions;

/// <summary>
/// The earliest stage whose assumptions new evidence has invalidated. Only the two stages that precede implementation exist: the WHAT (what is to be
/// achieved) and the HOW (the strategy). There is no REVIEW or PROVE destination yet.
/// </summary>
public enum ReclassificationStage
{
    What,
    How,
}

/// <summary>
/// The durable record that new evidence made previously valid assumptions no longer current: the evidence, the earliest stage that must be reconsidered, why,
/// where it was raised, the semantic revision it descends from and the one it created, and exactly which approved gates it invalidated.
/// <para>
/// It is not a human decision, not CLARIFY, not a failed check and not a request for changes: it is the working agent's structured report that a stage the
/// workflow already settled (or never opened) must be (re)visited before implementation continues. History is never rewritten: the invalidated gates, the
/// implementation and the verification stay exactly as recorded and are simply no longer current (see <c>WorkflowTask</c>). Nothing here can lower rigor: there
/// is no record that removes a requirement, only one that adds or reopens one.
/// </para>
/// It holds bounded display text only; no transcript content.
/// </summary>
public sealed record Reclassification
{
    public const int MaxEvidenceLength = 1000;
    public const int MaxReasonLength = 500;
    public const int MaxSessionLength = 200;

    public Reclassification(
        ReclassificationId id,
        ReclassificationStage stage,
        string evidence,
        string reason,
        int turn,
        string? session,
        SemanticRevisionId priorSemanticRevisionId,
        SemanticRevisionId semanticRevisionId,
        ImplementationFingerprint workspace,
        bool provisionalWork,
        IEnumerable<GateId> invalidatedGates)
    {
        if (!Enum.IsDefined(stage))
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown reclassification stage.");

        if (turn < 1)
            throw new ArgumentOutOfRangeException(nameof(turn), turn, "Turns are numbered from 1.");

        if (priorSemanticRevisionId == semanticRevisionId)
            throw new InvalidRevisionLineageException("A reclassification creates a semantic revision different from the one it descends from.");

        Id = id;
        Stage = stage;
        Evidence = Bounded(evidence, MaxEvidenceLength, nameof(evidence));
        Reason = Bounded(reason, MaxReasonLength, nameof(reason));
        Turn = turn;
        Session = session is null ? null : Bounded(session, MaxSessionLength, nameof(session));
        PriorSemanticRevisionId = priorSemanticRevisionId;
        SemanticRevisionId = semanticRevisionId;
        Workspace = Guard.NotNull(workspace, nameof(workspace));
        ProvisionalWork = provisionalWork;
        InvalidatedGates = [.. Guard.NotNull(invalidatedGates, nameof(invalidatedGates))];

        if (InvalidatedGates.Distinct().Count() != InvalidatedGates.Length)
            throw new InvalidRevisionLineageException("A reclassification invalidates each gate at most once.");
    }

    public ReclassificationId Id { get; }

    /// <summary>The earliest stage that must be reconsidered before implementation may continue.</summary>
    public ReclassificationStage Stage { get; }

    /// <summary>The new evidence, as the agent reported it.</summary>
    public string Evidence { get; }

    /// <summary>Why that evidence means the stage's assumptions no longer hold.</summary>
    public string Reason { get; }

    /// <summary>The 1-based agent turn (within its session) that reported it. Operational correlation.</summary>
    public int Turn { get; }

    /// <summary>The agent session that reported it (an opaque, bounded runtime identifier); null when the runtime gave none. Operational correlation.</summary>
    public string? Session { get; }

    /// <summary>The semantic revision that was current when the evidence was reported.</summary>
    public SemanticRevisionId PriorSemanticRevisionId { get; }

    /// <summary>The one semantic revision this reclassification created.</summary>
    public SemanticRevisionId SemanticRevisionId { get; }

    /// <summary>The exact workspace state when the report was accepted: the deterministic baseline of the provisional work, which may not change until the rerouted stages are approved.</summary>
    public ImplementationFingerprint Workspace { get; }

    /// <summary>True when the workspace held edits that were not recorded as an implementation revision (work done under the assumptions now invalid).</summary>
    public bool ProvisionalWork { get; }

    /// <summary>The approved gates this reclassification invalidated, in the order the gates were opened. Empty for an additive escalation.</summary>
    public ImmutableArray<GateId> InvalidatedGates { get; }

    /// <summary>The bound stage name as it is spoken in reasons and by the CLI.</summary>
    public string StageName => Stage == ReclassificationStage.What ? "WHAT" : "HOW";

    /// <summary>
    /// The reason <paramref name="evidence"/> and <paramref name="reason"/> could not be recorded, or null when both are well formed: the very bounds Core enforces, so a runtime
    /// can refuse a report at its own boundary and never record one the controller would have to reject.
    /// </summary>
    public static string? TextProblem(string? evidence, string? reason)
    {
        try
        {
            _ = Bounded(evidence, MaxEvidenceLength, nameof(evidence));
            _ = Bounded(reason, MaxReasonLength, nameof(reason));
            return null;
        }
        catch (ArgumentException e)
        {
            return e.Message.Split(" (Parameter", 2)[0];
        }
    }

    internal static string Bounded(string? value, int maxLength, string name)
    {
        Guard.NotBlank(value, name);

        if (value!.Length > maxLength)
            throw new ArgumentException($"The {name} must not exceed {maxLength} characters (was {value.Length}).", name);

        if (value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new ArgumentException($"The {name} must not contain control characters.", name);

        return value;
    }

    public bool Equals(Reclassification? other)
        => other is not null
           && Id == other.Id
           && Stage == other.Stage
           && Evidence == other.Evidence
           && Reason == other.Reason
           && Turn == other.Turn
           && Session == other.Session
           && PriorSemanticRevisionId == other.PriorSemanticRevisionId
           && SemanticRevisionId == other.SemanticRevisionId
           && Workspace == other.Workspace
           && ProvisionalWork == other.ProvisionalWork
           && InvalidatedGates.SequenceEqual(other.InvalidatedGates);

    public override int GetHashCode() => HashCode.Combine(Id, Stage, SemanticRevisionId);
}

/// <summary>Why an approved gate is no longer current.</summary>
public enum GateInvalidationCause
{
    /// <summary>New evidence reported by the agent invalidated it (it is the targeted stage's approval).</summary>
    Reclassification,

    /// <summary>A reclassification of the WHAT invalidated the approved HOW that was derived from it.</summary>
    DependsOnInvalidatedWhat,

    /// <summary>A later WHAT was approved after this HOW, so the HOW was derived from a WHAT that has been superseded.</summary>
    DependsOnSupersededWhat,
}

/// <summary>An approved gate that is no longer current, with the lineage that says why. The approval itself stays in the gate history.</summary>
public sealed record GateInvalidation(Gate Gate, GateInvalidationCause Cause, string Reason, ReclassificationId? Reclassification, GateId? SupersededBy);

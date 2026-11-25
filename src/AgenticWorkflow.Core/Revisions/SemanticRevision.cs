using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Core.Revisions;

/// <summary>
/// Why a semantic revision exists. Canonical V1.1 causes are provided; projects may add their own.
/// Formatting, code repair, added tests and helper refactors are not semantic changes and have no kind.
/// </summary>
public sealed record SemanticChangeKind : StableIdentifier
{
    public SemanticChangeKind(string value)
        : base(value)
    {
    }

    public static SemanticChangeKind Initial { get; } = new("initial");

    public static SemanticChangeKind RequirementChange { get; } = new("requirement_change");

    public static SemanticChangeKind StrategyChange { get; } = new("strategy_change");

    public static SemanticChangeKind AuthorityChange { get; } = new("authority_change");

    public static SemanticChangeKind ConsequentialAssumption { get; } = new("consequential_assumption");

    public static SemanticChangeKind ProofDefinitionChange { get; } = new("proof_definition_change");

    public static SemanticChangeKind HumanRequestChanges { get; } = new("human_request_changes");

    /// <summary>The human answered a workflow CLARIFY question: the answer is authoritative task context from this revision on.</summary>
    public static SemanticChangeKind HumanClarification { get; } = new("human_clarification");

    /// <summary>The human approved a WHAT proposal: the approved WHAT is the authoritative statement of the task's intent from this revision on.</summary>
    public static SemanticChangeKind HumanWhatApproval { get; } = new("human_what_approval");

    /// <summary>The human sent a WHAT proposal back with feedback: the feedback is authoritative task context from this revision on, and the WHAT is not yet approved.</summary>
    public static SemanticChangeKind HumanWhatChanges { get; } = new("human_what_changes");

    /// <summary>The human approved a HOW proposal: the approved strategy is the authoritative statement of how the task is to be implemented from this revision on.</summary>
    public static SemanticChangeKind HumanHowApproval { get; } = new("human_how_approval");

    /// <summary>The human sent a HOW proposal back with feedback: the feedback is authoritative task context from this revision on, and the strategy is not yet approved.</summary>
    public static SemanticChangeKind HumanHowChanges { get; } = new("human_how_changes");

    /// <summary>
    /// The working agent reported new evidence that invalidated the assumptions of a stage: the workflow is rerouted to the earliest invalid stage and everything that
    /// depended on it is no longer current. Created only together with its <c>reclassification_recorded</c> event; never by a plain semantic-revision event.
    /// </summary>
    public static SemanticChangeKind AgentReclassification { get; } = new("agent_reclassification");
}

/// <summary>
/// An immutable marker that the authoritative meaning of a task changed (requirements, approved strategy,
/// authority, consequential assumptions, proof definition). It holds no specification content, only identity,
/// lineage and provenance. Human approvals will bind to these; verification evidence binds to
/// <see cref="ImplementationRevision"/> instead.
/// <para>
/// Lineage is linear: ordinal 1 is the initial revision (no parent, kind <see cref="SemanticChangeKind.Initial"/>);
/// every later revision has ordinal = parent ordinal + 1 and a parent.
/// </para>
/// </summary>
public sealed record SemanticRevision
{
    public SemanticRevision(
        SemanticRevisionId id,
        TaskId taskId,
        int ordinal,
        SemanticRevisionId? parentId,
        SemanticChangeKind kind,
        string explanation,
        DateTimeOffset createdAt)
    {
        Guard.NotNull(kind, nameof(kind));

        if (ordinal < 1)
            throw new InvalidRevisionLineageException($"Semantic revision ordinal must be at least 1 (was {ordinal}).");

        if (ordinal == 1 != (parentId is null))
            throw new InvalidRevisionLineageException(
                "Exactly the initial semantic revision (ordinal 1) has no parent.");

        if (ordinal == 1 != (kind == SemanticChangeKind.Initial))
            throw new InvalidRevisionLineageException(
                $"Kind '{SemanticChangeKind.Initial}' is reserved for the initial semantic revision (ordinal 1).");

        if (parentId == id)
            throw new InvalidRevisionLineageException("A semantic revision cannot be its own parent.");

        Id = id;
        TaskId = taskId;
        Ordinal = ordinal;
        ParentId = parentId;
        Kind = kind;
        Explanation = Guard.NotBlank(explanation, nameof(explanation));
        CreatedAt = createdAt;
    }

    public SemanticRevisionId Id { get; }

    public TaskId TaskId { get; }

    public int Ordinal { get; }

    public SemanticRevisionId? ParentId { get; }

    public SemanticChangeKind Kind { get; }

    public string Explanation { get; }

    public DateTimeOffset CreatedAt { get; }
}

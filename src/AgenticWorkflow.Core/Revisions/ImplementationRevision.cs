using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Core.Revisions;

/// <summary>
/// Opaque, externally supplied identity of an exact implementation state (a tree hash, a snapshot hash, a
/// runtime revision, ...). Core stores it verbatim and never parses, normalizes or compares it for meaning.
/// </summary>
public sealed record ImplementationFingerprint
{
    public ImplementationFingerprint(string value) => Value = Guard.NotBlank(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>
/// An exact implementation state that work or verification refers to. Distinct from
/// <see cref="SemanticRevision"/>: implementation-only changes (repairs, added tests, refactors) create these
/// and never a semantic revision. Within a task the lineage is linear: the first has no parent, each later one
/// has the previous current revision as its parent.
/// <para>
/// Every implementation revision permanently records the <see cref="SemanticRevisionId"/> that was current when
/// it was produced. A later semantic revision never changes it; the kernel only makes the relationship
/// observable (<c>CurrentImplementationRevision.SemanticRevisionId != CurrentSemanticRevisionId</c>) and
/// attaches no meaning to the difference.
/// </para>
/// </summary>
public sealed record ImplementationRevision
{
    public ImplementationRevision(
        ImplementationRevisionId id,
        TaskId taskId,
        SemanticRevisionId semanticRevisionId,
        ImplementationFingerprint fingerprint,
        ImplementationRevisionId? parentId,
        DateTimeOffset createdAt)
    {
        if (parentId == id)
            throw new InvalidRevisionLineageException("An implementation revision cannot be its own parent.");

        Id = id;
        TaskId = taskId;
        SemanticRevisionId = semanticRevisionId;
        Fingerprint = Guard.NotNull(fingerprint, nameof(fingerprint));
        ParentId = parentId;
        CreatedAt = createdAt;
    }

    public ImplementationRevisionId Id { get; }

    public TaskId TaskId { get; }

    /// <summary>The semantic revision this implementation state was produced against.</summary>
    public SemanticRevisionId SemanticRevisionId { get; }

    public ImplementationFingerprint Fingerprint { get; }

    public ImplementationRevisionId? ParentId { get; }

    public DateTimeOffset CreatedAt { get; }
}

namespace AgenticWorkflow.Core.Identity;

// Generated/opaque identities. Each is a thin, typed wrapper over a non-empty GUID so that a task id can
// never be passed where a revision id is expected. They carry no persistence meaning.

public readonly record struct TaskId
{
    public TaskId(Guid value) => Value = Guard.NotEmpty(value, nameof(value));

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public readonly record struct SemanticRevisionId
{
    public SemanticRevisionId(Guid value) => Value = Guard.NotEmpty(value, nameof(value));

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ImplementationRevisionId
{
    public ImplementationRevisionId(Guid value) => Value = Guard.NotEmpty(value, nameof(value));

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public readonly record struct EventId
{
    public EventId(Guid value) => Value = Guard.NotEmpty(value, nameof(value));

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public readonly record struct VerificationRunId
{
    public VerificationRunId(Guid value) => Value = Guard.NotEmpty(value, nameof(value));

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public readonly record struct GateId
{
    public GateId(Guid value) => Value = Guard.NotEmpty(value, nameof(value));

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ReclassificationId
{
    public ReclassificationId(Guid value) => Value = Guard.NotEmpty(value, nameof(value));

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ReviewId
{
    public ReviewId(Guid value) => Value = Guard.NotEmpty(value, nameof(value));

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ReviewRequestId
{
    public ReviewRequestId(Guid value) => Value = Guard.NotEmpty(value, nameof(value));

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ProofContractId
{
    public ProofContractId(Guid value) => Value = Guard.NotEmpty(value, nameof(value));

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ProofId
{
    public ProofId(Guid value) => Value = Guard.NotEmpty(value, nameof(value));

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

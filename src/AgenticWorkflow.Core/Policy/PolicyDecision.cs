using AgenticWorkflow.Core.Authority;
using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Core.Policy;

/// <summary>What a policy rule did. The four V1.1 effects.</summary>
public enum PolicyEffect
{
    /// <summary>The action was stopped.</summary>
    Prevent,

    /// <summary>Something must happen (e.g. human authority) before proceeding.</summary>
    Require,

    /// <summary>A condition was observed and recorded; nothing was blocked.</summary>
    Detect,

    /// <summary>Someone was informed; nothing was blocked.</summary>
    Notify,
}

/// <summary>Identifies the policy or rule that produced a decision.</summary>
public sealed record PolicyRuleId : StableIdentifier
{
    public PolicyRuleId(string value)
        : base(value)
    {
    }
}

/// <summary>
/// What caused a rule to fire: a human-readable description, optionally the semantic action category involved
/// and an opaque reference (an event id, evidence id, tool-call id, ...) that Core does not interpret.
/// </summary>
public sealed record PolicyTrigger
{
    public PolicyTrigger(string description, ActionId? action = null, string? reference = null)
    {
        Description = Guard.NotBlank(description, nameof(description));
        Action = action;
        Reference = reference;
    }

    public string Description { get; }

    public ActionId? Action { get; }

    public string? Reference { get; }
}

/// <summary>
/// Durable record of the <em>result</em> of a policy rule: what happened (<see cref="Effect"/>), why
/// (<see cref="Explanation"/>), what triggered it, and which rule was the source. Evaluating rules is not part
/// of the kernel. The optional revision ids tie the decision to the semantic/implementation state it concerned.
/// </summary>
public sealed record PolicyDecision
{
    public PolicyDecision(
        TaskId taskId,
        PolicyRuleId rule,
        PolicyEffect effect,
        PolicyTrigger trigger,
        string explanation,
        DateTimeOffset decidedAt,
        SemanticRevisionId? semanticRevisionId = null,
        ImplementationRevisionId? implementationRevisionId = null)
    {
        if (!Enum.IsDefined(effect))
            throw new ArgumentOutOfRangeException(nameof(effect), effect, "Unknown policy effect.");

        TaskId = taskId;
        Rule = Guard.NotNull(rule, nameof(rule));
        Effect = effect;
        Trigger = Guard.NotNull(trigger, nameof(trigger));
        Explanation = Guard.NotBlank(explanation, nameof(explanation));
        DecidedAt = decidedAt;
        SemanticRevisionId = semanticRevisionId;
        ImplementationRevisionId = implementationRevisionId;
    }

    public TaskId TaskId { get; }

    public PolicyRuleId Rule { get; }

    public PolicyEffect Effect { get; }

    public PolicyTrigger Trigger { get; }

    public string Explanation { get; }

    public DateTimeOffset DecidedAt { get; }

    public SemanticRevisionId? SemanticRevisionId { get; }

    public ImplementationRevisionId? ImplementationRevisionId { get; }
}

namespace AgenticWorkflow.Core.Gates;

/// <summary>
/// The question of a CLARIFY gate: what the agent needs a human to answer and why it matters. Deliberately small and bounded, so an agent's prose can never
/// grow workflow history without limit, and single-purpose: one question, one reason. It holds no transcript excerpt.
/// </summary>
public sealed record ClarificationRequest
{
    public const int MaxQuestionLength = 500;
    public const int MaxReasonLength = 500;

    public ClarificationRequest(string question, string reason, int turn)
    {
        Question = Bounded(question, MaxQuestionLength, nameof(question));
        Reason = Bounded(reason, MaxReasonLength, nameof(reason));

        if (turn < 1)
            throw new ArgumentOutOfRangeException(nameof(turn), turn, "Turns are numbered from 1.");

        Turn = turn;
    }

    /// <summary>What the human is asked.</summary>
    public string Question { get; }

    /// <summary>Why the answer materially affects the task.</summary>
    public string Reason { get; }

    /// <summary>The 1-based agent turn (within its session) in which the question was raised. Operational correlation; it identifies no conversation.</summary>
    public int Turn { get; }

    private static string Bounded(string? value, int maxLength, string name)
    {
        Guard.NotBlank(value, name);

        if (value!.Length > maxLength)
            throw new ArgumentException($"The {name} must not exceed {maxLength} characters (was {value.Length}).", name);

        if (value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new ArgumentException($"The {name} must not contain control characters.", name);

        return value;
    }
}

using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Application.Acceptance;

/// <summary>
/// The person's side of the workflow-owned CLARIFY gate. It works from nothing but the durable event stream, so it is usable from any later process: the
/// question does not need the agent session that raised it.
/// <para>
/// An answer is explicit about <em>which</em> question it answers: the caller must name the question (gate) id, and the answer is refused unless that is the
/// run's open CLARIFY gate and it is still current (asked under the current semantic revision). Free text is valid here because it is an answer to a named
/// question, not an approval; no text is ever read as a decision. A refusal records nothing, so a repeated or stale answer can never add a second decision or
/// a second revision. Cancelling a question is <see cref="AcceptGateService.CancelAsync"/> (<c>aw abort</c>).
/// </para>
/// </summary>
public sealed class ClarificationService
{
    private readonly WorkflowTaskService _tasks;

    public ClarificationService(WorkflowTaskService tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        _tasks = tasks;
    }

    /// <summary>Checks, without recording anything, that <paramref name="questionId"/> is this run's open and current question. Returns it.</summary>
    public async Task<Gate> RequireAnswerableAsync(TaskId taskId, GateId questionId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _tasks.LoadAsync(taskId, cancellationToken).ConfigureAwait(false);

        return RequireAnswerable(snapshot.Task, questionId);
    }

    /// <param name="questionId">The question being answered. Required: an answer never defaults to "the open question".</param>
    /// <param name="answer">The human's answer, bounded by <see cref="GateDecision.MaxAnswerLength"/>. Recorded trimmed, otherwise exactly as given.</param>
    public async Task<GateOutcome> AnswerAsync(TaskId taskId, GateId questionId, string answer, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);

        var trimmed = answer.Trim();
        if (trimmed.Length > GateDecision.MaxAnswerLength)
            throw new ArgumentException($"An answer must not exceed {GateDecision.MaxAnswerLength} characters (was {trimmed.Length}).", nameof(answer));

        var snapshot = await _tasks.LoadAsync(taskId, cancellationToken).ConfigureAwait(false);
        var gate = RequireAnswerable(snapshot.Task, questionId);

        var change = await AcceptGateService.DecideAsync(() => _tasks.AnswerClarificationAsync(snapshot, gate.Id, trimmed, cancellationToken)).ConfigureAwait(false);
        return new GateOutcome(gate, change);
    }

    private static Gate RequireAnswerable(Core.Lifecycle.WorkflowTask task, GateId questionId)
    {
        var gate = AcceptGateService.Resolve(task, questionId, GateType.Clarify);

        if (task.PendingGate?.Id != gate.Id)
            throw new GateRefusedException(GateRefusal.NoPendingGate, $"Question {gate.Id} is not the open question of run {task.Id}.");

        if (!task.IsCurrent(gate))
        {
            throw new GateRefusedException(
                GateRefusal.NotCurrent,
                $"Question {gate.Id} was asked under an earlier semantic revision of run {task.Id} and no longer describes it; it cannot be answered. Nothing was recorded.");
        }

        return gate;
    }
}

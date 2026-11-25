using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;

namespace AgenticWorkflow.Application.Acceptance;

/// <summary>
/// The person's approval side of the workflow-owned HOW gate, exactly parallel to <see cref="WhatService"/>. It works from nothing but the durable event
/// stream, so it is usable from any later process: the proposal does not need the agent session that raised it. (Sending the proposal back is
/// <see cref="AcceptGateService.RequestChangesAsync"/>; stopping is <see cref="AcceptGateService.CancelAsync"/>.)
/// <para>
/// An approval is an explicit structured action about <em>which</em> gate: the caller must name the HOW gate id, and it is refused unless that is the run's open
/// HOW gate and it is still current (proposed under the current semantic revision, which, when the WHAT is also required, already includes the approved WHAT).
/// No text is read as an approval. A refusal records nothing, so a repeated or stale approval can never add a second decision or a second revision. An approval
/// records no implementation, verifies nothing and completes nothing.
/// </para>
/// </summary>
public sealed class HowService
{
    private readonly WorkflowTaskService _tasks;

    public HowService(WorkflowTaskService tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        _tasks = tasks;
    }

    /// <summary>Checks, without recording anything, that <paramref name="gateId"/> is this run's open and current HOW proposal. Returns it.</summary>
    public async Task<Gate> RequireApprovableAsync(TaskId taskId, GateId gateId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _tasks.LoadAsync(taskId, cancellationToken).ConfigureAwait(false);

        return RequireApprovable(snapshot.Task, gateId);
    }

    /// <param name="gateId">The HOW gate being approved. Required: an approval never defaults to "the open gate".</param>
    public async Task<GateOutcome> ApproveAsync(TaskId taskId, GateId gateId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _tasks.LoadAsync(taskId, cancellationToken).ConfigureAwait(false);
        var gate = RequireApprovable(snapshot.Task, gateId);

        var change = await AcceptGateService.DecideAsync(() => _tasks.ApproveHowAsync(snapshot, gate.Id, cancellationToken)).ConfigureAwait(false);
        return new GateOutcome(gate, change);
    }

    private static Gate RequireApprovable(WorkflowTask task, GateId gateId)
    {
        var gate = AcceptGateService.Resolve(task, gateId, GateType.How);

        if (task.PendingGate?.Id != gate.Id)
            throw new GateRefusedException(GateRefusal.NoPendingGate, $"HOW proposal {gate.Id} is not the open gate of run {task.Id}.");

        if (!task.IsCurrent(gate))
        {
            throw new GateRefusedException(
                GateRefusal.NotCurrent,
                $"HOW proposal {gate.Id} was made under an earlier semantic revision of run {task.Id} and no longer describes it; it cannot be approved. Nothing was recorded.");
        }

        return gate;
    }
}

using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Cli.State;

/// <summary>
/// Gives the direct-path runner a way to keep the workspace baseline of a CLARIFY question (<see cref="IContinuationBaselineSink"/>) in the same
/// <c>continuation.json</c> a requested-changes continuation uses, keyed by the question's gate id. The file is written atomically; a failure is thrown so the
/// runner does not open a question that could not be resumed with its scope intact.
/// </summary>
internal sealed class ContinuationBaselineSink(ContinuationBaselineStore store) : IContinuationBaselineSink
{
    public Task SaveAsync(TaskId task, GateId gate, ImplementationState baseline, CancellationToken cancellationToken = default)
    {
        store.Save(task, gate, baseline);

        return Task.CompletedTask;
    }
}

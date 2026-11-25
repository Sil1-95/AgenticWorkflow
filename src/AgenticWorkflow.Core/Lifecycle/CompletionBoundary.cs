using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Core.Lifecycle;

/// <summary>
/// How far the requested outcome must be carried operationally. Independent of <see cref="WorkflowState"/>.
/// The V1.1 boundaries are provided as canonical values; projects may define their own with the same
/// identifier rules (e.g. <c>new CompletionBoundary("staging_deploy")</c>). Core performs no Git or release
/// operations and does not rank boundaries.
/// </summary>
public sealed record CompletionBoundary : StableIdentifier
{
    public CompletionBoundary(string value)
        : base(value)
    {
    }

    public static CompletionBoundary WorkingTree { get; } = new("working_tree");

    public static CompletionBoundary LocalCommit { get; } = new("local_commit");

    public static CompletionBoundary IntegratedBranch { get; } = new("integrated_branch");

    public static CompletionBoundary RemoteBranch { get; } = new("remote_branch");

    public static CompletionBoundary PullRequest { get; } = new("pull_request");

    public static CompletionBoundary Release { get; } = new("release");

    public static CompletionBoundary Deployment { get; } = new("deployment");
}

using AgenticWorkflow.Application.Acceptance;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Controller;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.Runtime;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;

namespace AgenticWorkflow.Cli.Commands;

/// <summary>
/// What every command that continues a run's agent work (<c>aw request-changes</c>, <c>aw continue</c>) checks before it changes anything: the run's project is
/// still the one the run was started under, its prerequisites are met, the workspace is usable, and the recorded agent conversation can be trusted. One
/// definition, so a retry is held to exactly the rules the original continuation was and can never widen what the run is allowed to do.
/// </summary>
internal static class ContinuationPreflight
{
    /// <summary>The project the run was started under. Refused when its configuration changed since (scope and checks must stay as they were) or a prerequisite is missing.</summary>
    public static ResolvedProject ResolveProject(CliServices services, RunStore store, TaskId runId)
    {
        var (metadata, metadataProblem) = store.ReadMetadata(runId);

        if (metadata is null)
            throw new ConfigurationException($"The run's metadata is unavailable ({metadataProblem}), so its project cannot be resolved to continue the work.");

        var project = ProjectResolver.Resolve(metadata.Workspace, metadata.ConfigPath, services.CurrentDirectory);

        // What fixes the run's scope and checks must be as it was. A run that recorded that (every run started since profiles became bindings) is held to exactly that, so editing a
        // profile, or the project's binding of a role to one, never stops it: the run's own recorded bindings are what it continues with. An older run recorded only the whole file's hash and is held to that.
        var unchanged = metadata.SemanticsSha256 is { } recorded
            ? string.Equals(project.SemanticsSha256, recorded, StringComparison.Ordinal)
            : string.Equals(project.ConfigSha256, metadata.ConfigSha256, StringComparison.Ordinal);

        if (!unchanged)
            throw new ConfigurationException($"The configuration '{metadata.ConfigPath}' changed since this run started; the run's scope and checks must stay as they were. Restore it, or start a new run.");

        var unmet = services.RuntimeFactory.CheckPrerequisites(project).Where(p => !p.IsMet).ToList();

        if (unmet.Count > 0)
            throw new ConfigurationException([.. unmet.Select(p => $"{p.Name}: {p.Problem}")]);

        return project;
    }

    /// <summary>
    /// What a continuation needs of the run's recorded bindings, checked before it records or starts anything: the root role is bound, every recorded binding is one this build can
    /// launch exactly as recorded (its harness, its model, its reasoning), and a stage that is due has the role it needs. A binding that cannot be honoured is refused naming the
    /// profile and the value; nothing is replaced by the project's configuration as it is now.
    /// </summary>
    public static void RequireLaunchable(CliServices services, WorkflowTask task)
    {
        if (!task.RoleBindings.ContainsKey(DirectPathRoles.RootRoleId))
            throw new BindingRefusedException(ControllerErrorCodes.MissingBinding, $"The run has no binding for its '{DirectPathRoles.RootRoleId}' role, so no agent can be started for it. Bind it with 'aw bind <run-id> --role root --profile <profile>'.");

        foreach (var binding in RunBindings.From(task).All)
        {
            if (!Harnesses.IsSupported(binding.Execution.Harness))
                throw new BindingRefusedException(ControllerErrorCodes.UnsupportedHarness, $"The run's '{binding.Role}' role is bound to profile '{binding.Profile}', whose harness '{binding.Execution.Harness}' this build cannot run. Nothing was started.");

            var problems = services.RuntimeFactory.ValidateExecution(binding.Profile, binding.Execution);

            if (problems.Count > 0)
                throw new BindingRefusedException(ControllerErrorCodes.UnsupportedConfiguration, $"The run's '{binding.Role}' role is bound to profile '{binding.Profile}', which cannot be launched as it was recorded: {string.Join("; ", problems)}. Nothing was started.");
        }

        if (task.IsReviewDue && !task.RoleBindings.ContainsKey(DirectPathRoles.ReviewerRoleId))
            throw MissingStage("reviewer", "an independent review is due");

        if (task.IsProveDue && !task.RoleBindings.ContainsKey(DirectPathRoles.ProverRoleId))
            throw MissingStage("prover", "a formal proof is due");
    }

    private static BindingRefusedException MissingStage(string role, string why)
        => new(ControllerErrorCodes.MissingBinding, $"The run needs its '{role}' role bound ({why}) and it has none; no profile was chosen for it. Bind it with 'aw bind <run-id> --role {role} --profile <profile>', then continue.");

    /// <summary>The workspace as it is now; a workspace that cannot be fingerprinted cannot be worked in.</summary>
    public static async Task<ImplementationState> CaptureWorkspaceAsync(CliServices services, ResolvedProject project, CancellationToken cancellationToken)
    {
        try
        {
            return await services.Workspace.CaptureAsync(project.Workspace, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or DirectoryNotFoundException)
        {
            throw new ConfigurationException($"The workspace cannot be used: {e.Message}");
        }
    }

    /// <summary>
    /// A recorded conversation that cannot be trusted is refused now: resuming it is unsafe and silently starting fresh would lose the context. (No record at
    /// all is fine: that is a run from before native continuity, which continues in an explicit fresh session.)
    /// </summary>
    public static void RequireUsableConversation(CliServices services, RunStore store, TaskId runId, ResolvedProject project, WorkflowTask task)
    {
        try
        {
            var recorded = new RunConversationStore(store, services.Clock).Find(runId);

            // The conversation must be the root role's own, under the profile the run recorded for it (not the one the configuration names today).
            if (recorded is not null && task.RoleBindings.TryGetValue(DirectPathRoles.RootRoleId, out var root) && recorded.Conversation.Profile != root.Profile.Id)
                throw new InvalidAgentConversationException($"it belongs to profile '{recorded.Conversation.Profile}', not '{root.Profile.Id}'");
        }
        catch (InvalidAgentConversationException e)
        {
            throw new GateRefusedException(GateRefusal.ConversationUnusable, $"The run's recorded agent conversation cannot be used ({e.Message}). It was not resumed and no fresh session was started in its place.");
        }
    }
}

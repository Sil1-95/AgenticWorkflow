using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Cli.Commands;

/// <summary>
/// <c>aw run</c>: resolves the project, creates one durable run, and hands it to the existing <see cref="DirectPathRunner"/> through <see cref="RunExecutor"/>.
/// This class composes and reports; it contains no execution logic of its own (no turn loop, no verification, no completion rule). A run's acceptance requirement is
/// explicit (<c>--acceptance</c>, else the project's default) and persisted with the run: with none a verified run is completed by the controller (exit 0), with
/// <c>human</c> it ends AWAITING_HUMAN at the ACCEPT gate (exit 17) and only <c>aw accept</c> completes it.
/// <para>
/// Order matters: everything that can be refused is refused first (usage, configuration, prerequisites, a workspace Git cannot fingerprint, a
/// state root inside the repository), so a mistake never creates a run or starts an agent.
/// </para>
/// <para>
/// The command is two steps so that one definition serves both owners of a run: <see cref="PrepareAsync"/> (refuse, create, hold the run's lock) and the executor. The CLI
/// runs both; the controller prepares, then supervises the executor in the background.
/// </para>
/// </summary>
internal sealed class RunCommand(CliServices services)
{
    public async Task<int> ExecuteAsync(RunRequest request, CancellationToken cancellationToken)
    {
        using var stretch = await PrepareAsync(request, services.Out, cancellationToken).ConfigureAwait(false);

        // ---- 3. the existing direct path, over this run's own runtime, then the report ----------------------------------------
        return await new RunExecutor(services).ExecuteAsync(stretch.Execution, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Everything of <c>aw run</c> up to the agent starting: the refusals, the durable run, and the run's live lock, which the returned stretch holds until it is disposed.
    /// <paramref name="banner"/> receives the lines that describe the new run.
    /// </summary>
    public async Task<PreparedStretch> PrepareAsync(RunRequest request, TextWriter banner, CancellationToken cancellationToken)
    {
        var timing = new RunTiming();

        // ---- 1. refuse what can be refused before anything exists ---------------------------------------------------------
        var objective = ReadObjective(request);
        var project = ProjectResolver.Resolve(request.Workspace, request.Config, services.CurrentDirectory);

        var unmet = services.RuntimeFactory.CheckPrerequisites(project).Where(p => !p.IsMet).ToList();
        if (unmet.Count > 0)
            throw new ConfigurationException([.. unmet.Select(p => $"{p.Name}: {p.Problem}")]);

        var store = new RunStore(RunStore.ResolveRoot(services.GetEnvironmentVariable, services.LocalApplicationData));
        if (RunStore.IsInside(store.Root, project.Workspace))
            throw new ConfigurationException($"The state root '{store.Root}' is inside the workspace '{project.Workspace}'. Run state must live outside the repository the agent edits; set {CliEnvironment.HomeVariable}.");

        ImplementationState baseline;
        try
        {
            baseline = await services.Workspace.CaptureAsync(project.Workspace, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or DirectoryNotFoundException)
        {
            throw new ConfigurationException($"The workspace cannot be used: {e.Message}");
        }

        // Every role the run starts with is bound to a profile that was named for it (by the request, else by the project's configuration) and checked against its harness now, before any
        // state exists or any agent starts. A required reviewer or prover that nobody named a profile for is refused: the implementer's profile is never substituted.
        var resolved = RoleBindingResolver.ResolveForStart(
            project,
            request.Bindings ?? new Dictionary<RoleId, ProfileId>(),
            request.RequireReview,
            request.RequireProve,
            services.RuntimeFactory);

        // ---- 2. one durable run: state outside the repository, task and role binding in the event stream ----------------------
        var runId = new TaskId(services.Ids.Next());
        var definition = project.ToDefinition(objective, resolved);

        // The one place the acceptance requirement is decided: the command line wins over the project's default. From here on it is the run's own
        // semantics, persisted in its first event; later configuration changes cannot reach it.
        var acceptance = request.Acceptance ?? project.Config.Acceptance;
        var acceptanceSource = request.Acceptance is null ? "project default" : "--acceptance";

        store.Create(runId, new RunMetadata(RunMetadata.CurrentVersion, runId.ToString(), project.Workspace, services.Clock.UtcNow, project.ConfigPath, project.ConfigSha256, project.SemanticsSha256));

        // Held until the stretch ends: while it is, the run's agent session (if one is waiting for a person) can still be answered natively.
        var liveLock = store.AcquireLiveLock(runId);

        try
        {
            var tasks = await store.OpenNewAsync(runId, cancellationToken).ConfigureAwait(false);
            var created = await tasks.CreateAsync(runId, objective, CompletionBoundary.WorkingTree, acceptance, cancellationToken, whatRequired: request.RequireWhat, howRequired: request.RequireHow, reviewRequired: request.RequireReview, proveRequired: request.RequireProve).ConfigureAwait(false);

            // The resolved bindings are part of the run's history from the first moment, each with the execution configuration it was resolved to: what the run launches, and what it
            // resumes with, never the project's configuration as it may be later.
            var snapshot = created.Snapshot;

            foreach (var binding in resolved)
                snapshot = (await tasks.BindRoleAsync(snapshot, binding.Role, binding.CoreProfile, cancellationToken, binding.Execution).ConfigureAwait(false)).Snapshot;

            banner.WriteLine($"Run: {runId}");
            banner.WriteLine($"Workspace: {project.Workspace}");

            foreach (var binding in resolved)
                banner.WriteLine($"{Label(binding.Role)}: {binding.Profile.Id} ({Describe(binding.Execution)})");

            banner.WriteLine($"Acceptance: {AcceptancePolicy.Describe(acceptance)} ({acceptanceSource})");

            if (request.RequireWhat)
                banner.WriteLine("Required: WHAT approval (--require what): the agent must propose the WHAT and you must approve it before anything is implemented");

            if (request.RequireHow)
                banner.WriteLine(request.RequireWhat
                    ? "Required: HOW approval (--require how): after the WHAT is approved, the agent must propose its implementation strategy and you must approve it before anything is implemented"
                    : "Required: HOW approval (--require how): the agent must propose its implementation strategy and you must approve it before anything is implemented");

            if (request.RequireReview)
                banner.WriteLine($"Required: independent REVIEW (--require review): after verification passes, a fresh reviewer session ({resolved.First(b => b.Role.Id == DirectPathRoles.ReviewerRoleId).Profile.Id}) judges the implementation before the run completes");

            if (request.RequireProve)
                banner.WriteLine($"Required: formal PROVE (--require prove): the agent must submit a Proof Contract; after verification passes{(request.RequireReview ? " and the review passes" : string.Empty)}, a fresh prover session ({resolved.First(b => b.Role.Id == DirectPathRoles.ProverRoleId).Profile.Id}) proves it before the run completes");

            banner.WriteLine($"State: {Format.State(WorkflowState.Running)}");
            banner.WriteLine();

            return new PreparedStretch(new RunExecution(runId, project, definition, tasks, store, baseline, request.Presence, RunStretch.Initial, timing), liveLock);
        }
        catch
        {
            liveLock.Dispose();

            throw;
        }
    }

    /// <summary>How a binding launches, in words: the harness, then the model and reasoning it asks for (or that it asks for none, so the harness's own setting applies).</summary>
    internal static string Describe(ExecutionConfiguration execution)
        => $"{execution.Harness}, model {execution.Model ?? "not set (harness default)"}, reasoning {execution.Reasoning ?? "not set (harness default)"}";

    private static string Label(Role role)
        => role.Id == DirectPathRoles.RootRoleId ? "Profile" : $"{char.ToUpperInvariant(role.Id.Value[0])}{role.Id.Value[1..]} profile";

    private string ReadObjective(RunRequest request)
    {
        if (request.TaskFile is null)
            return request.TaskText!.Trim();

        var path = Path.GetFullPath(request.TaskFile, services.CurrentDirectory);

        if (!File.Exists(path))
            throw new UsageException($"The task file '{path}' does not exist.");

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UsageException($"The task file '{path}' could not be read: {e.Message}");
        }

        return string.IsNullOrWhiteSpace(text) ? throw new UsageException($"The task file '{path}' is empty.") : text.Trim();
    }
}

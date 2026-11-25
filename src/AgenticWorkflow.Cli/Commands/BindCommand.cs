using AgenticWorkflow.Application.Controller;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Persistence.Sqlite;

namespace AgenticWorkflow.Cli.Commands;

/// <summary>
/// <c>aw bind &lt;run-id&gt; --role &lt;role&gt; --profile &lt;profile&gt;</c>: binds a role the run needs (the reviewer or prover an agent asked for mid-run, shown by <c>aw status</c> as a missing
/// binding) to a profile the project defines, and records the execution configuration the profile resolves to now. It starts nothing: the person continues the run afterwards
/// (<c>aw continue</c>). It never chooses a profile and never replaces a binding: a role that already has a different binding is refused, one that already has this one is left as it is.
/// </summary>
internal sealed class BindCommand(CliServices services)
{
    public async Task<int> ExecuteAsync(BindRequest request, CancellationToken cancellationToken)
    {
        var runId = CommandLine.ParseRunId(request.RunId);

        try
        {
            var recorded = await RecordAsync(runId, request.Role, request.Profile, cancellationToken).ConfigureAwait(false);

            services.Out.WriteLine($"Run: {runId}");
            services.Out.WriteLine(recorded.Changed
                ? $"Role '{request.Role}' is now bound to profile '{request.Profile}' ({RunCommand.Describe(recorded.Binding.Execution)}). Nothing was started; continue the run with 'aw continue {runId}'."
                : $"Role '{request.Role}' was already bound to profile '{request.Profile}' ({RunCommand.Describe(recorded.Binding.Execution)}). Nothing was changed.");

            return ExitCodes.Success;
        }
        catch (BindingRefusedException e)
        {
            services.Error.WriteLine($"BINDING REFUSED ({e.Code}): {e.Message}");
            services.Error.WriteLine("Nothing was recorded.");

            return e.Code is ControllerErrorCodes.AlreadyBound ? ExitCodes.GateDecisionRefused : ExitCodes.UsageOrConfiguration;
        }
        catch (RunStateException e)
        {
            services.Error.WriteLine($"{(e.Problem == RunStateProblem.NotFound ? "RUN NOT FOUND" : "RUN STATE UNREADABLE")}: {e.Message}");

            return e.Problem == RunStateProblem.NotFound ? ExitCodes.RunNotFound : ExitCodes.RunStateUnreadable;
        }
        catch (Exception e) when (e is EventStoreException or DomainException or Microsoft.Data.Sqlite.SqliteException)
        {
            services.Error.WriteLine($"RUN STATE UNREADABLE: {e.Message}");
            services.Error.WriteLine("Nothing was repaired or guessed; the run's files were left untouched.");

            return ExitCodes.RunStateUnreadable;
        }
    }

    /// <summary>
    /// Resolves <paramref name="profile"/> against the project the run was started under (refusing what the harness cannot honour), then records the binding, holding the run's lock so
    /// that nobody is working on it meanwhile (a launched agent's configuration is fixed when it starts). Throws <see cref="BindingRefusedException"/> or <see cref="UsageException"/>;
    /// nothing was recorded then.
    /// </summary>
    public async Task<BindOutcome> RecordAsync(TaskId runId, RoleId roleId, ProfileId profile, CancellationToken cancellationToken)
    {
        var store = new RunStore(RunStore.ResolveRoot(services.GetEnvironmentVariable, services.LocalApplicationData));
        var tasks = await store.OpenExistingAsync(runId, cancellationToken).ConfigureAwait(false);

        var role = BindableRoles.Find(roleId.Value) ?? throw new BindingRefusedException(ControllerErrorCodes.InvalidArgument, $"Role '{roleId}' cannot be bound (known: {string.Join(", ", BindableRoles.All)}).");

        using var liveLock = TryAcquireLiveLock(store, runId);

        var snapshot = await tasks.FindAsync(runId, cancellationToken).ConfigureAwait(false)
                       ?? throw new RunStateException(RunStateProblem.Unreadable, $"The run's event stream holds no history for '{runId}'.");

        if (snapshot.Task.IsTerminal)
            throw new UsageException($"Run {runId} is {Format.State(snapshot.Task.WorkflowState)}; a finished run's roles are not bound.");

        var project = ContinuationPreflight.ResolveProject(services, store, runId);
        var resolved = RoleBindingResolver.Resolve(project, role, profile, services.RuntimeFactory);

        if (snapshot.Task.RoleBindings.TryGetValue(roleId, out var existing))
        {
            var recorded = existing.Execution ?? AgentProfileEntry.ClaudeCodeLocal.ToExecution();

            if (existing.Profile.Id == profile && recorded == resolved.Execution)
                return new BindOutcome(resolved, Changed: false);

            throw new BindingRefusedException(
                ControllerErrorCodes.AlreadyBound,
                $"Role '{roleId}' is already bound to profile '{existing.Profile.Id}' ({RunCommand.Describe(recorded)}); a binding is never replaced, so it was not bound to '{profile}'.");
        }

        await tasks.BindRoleAsync(snapshot, role, resolved.CoreProfile, cancellationToken, resolved.Execution).ConfigureAwait(false);

        return new BindOutcome(resolved, Changed: true);
    }

    private static IDisposable TryAcquireLiveLock(RunStore store, TaskId runId)
    {
        try
        {
            return store.AcquireLiveLock(runId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UsageException("This run is still live in another process; bind the role when it has stopped.");
        }
    }
}

/// <summary>The result of a binding: the resolved role and profile, and whether this call recorded it (false when the role already had exactly this binding).</summary>
internal sealed record BindOutcome(ResolvedRoleBinding Binding, bool Changed);

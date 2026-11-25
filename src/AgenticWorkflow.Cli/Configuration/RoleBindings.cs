using AgenticWorkflow.Application.Controller;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Cli.Runtime;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Cli.Configuration;

/// <summary>
/// A role could not be bound as asked, and nothing was recorded or launched. <see cref="Code"/> is one of <see cref="ControllerErrorCodes"/> (<c>missing_binding</c>,
/// <c>unknown_profile</c>, <c>unsupported_harness</c>, <c>unsupported_configuration</c>, <c>already_bound</c>), the same words a controller client sees.
/// </summary>
internal sealed class BindingRefusedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>A role together with the profile it is bound to: both ends of "this role is performed by this profile", with the launch values the profile carries.</summary>
internal sealed record ResolvedRoleBinding(Role Role, AgentProfileEntry Profile)
{
    public Profile CoreProfile => Profile.ToCoreProfile(Role);

    public ExecutionConfiguration Execution => Profile.ToExecution();
}

/// <summary>
/// Turns a choice "role X uses profile Y" into a binding that can be recorded, or refuses it. Nothing here picks a profile: a role is bound to the profile that was named for it (by
/// the request, else by the project's configuration) or it is not bound. A profile that does not exist, whose harness this build cannot run, or whose model or reasoning the harness
/// cannot honour, refuses the binding and says which; no other profile, harness, model or reasoning is substituted and nothing is dropped.
/// </summary>
internal static class RoleBindingResolver
{
    /// <summary>
    /// The bindings a new run starts with: the root role always; the reviewer and prover roles when the person required them, or named a profile for them (in the request or the
    /// project's configuration). A required role with no profile named anywhere is <c>missing_binding</c>.
    /// </summary>
    public static IReadOnlyList<ResolvedRoleBinding> ResolveForStart(
        ResolvedProject project,
        IReadOnlyDictionary<RoleId, ProfileId> requested,
        bool requireReview,
        bool requireProve,
        IAgentRuntimeFactory factory)
    {
        var unbindable = requested.Keys.FirstOrDefault(role => BindableRoles.Find(role.Value) is null);

        if (unbindable is not null)
            throw new BindingRefusedException(ControllerErrorCodes.InvalidArgument, $"Role '{unbindable}' cannot be bound (known: {string.Join(", ", BindableRoles.All)}).");

        var bindings = new List<ResolvedRoleBinding>
        {
            Resolve(project, DirectPathRoles.Root, Named(requested, DirectPathRoles.RootRoleId) ?? project.ProfileId, factory),
        };

        if ((Named(requested, DirectPathRoles.ReviewerRoleId) ?? project.ReviewerProfile?.Id) is { } reviewer)
            bindings.Add(Resolve(project, DirectPathRoles.Reviewer, reviewer, factory));
        else if (requireReview)
            throw Missing(project, DirectPathRoles.Reviewer, "--require review", "reviewerProfile");

        if ((Named(requested, DirectPathRoles.ProverRoleId) ?? project.ProverProfile?.Id) is { } prover)
            bindings.Add(Resolve(project, DirectPathRoles.Prover, prover, factory));
        else if (requireProve)
            throw Missing(project, DirectPathRoles.Prover, "--require prove", "proverProfile");

        return bindings;
    }

    /// <summary>One role bound to one named profile of the project's catalog, validated against the harness. Refuses with the reason.</summary>
    public static ResolvedRoleBinding Resolve(ResolvedProject project, Role role, ProfileId profile, IAgentRuntimeFactory factory)
    {
        var entry = project.Catalog.Find(profile.Value)
            ?? throw new BindingRefusedException(
                ControllerErrorCodes.UnknownProfile,
                $"Role '{role.Id}' is to use profile '{profile}', which the project does not define (known: {project.Catalog.KnownIds}). No other profile was used in its place.");

        if (!Harnesses.IsSupported(entry.Harness))
        {
            throw new BindingRefusedException(
                ControllerErrorCodes.UnsupportedHarness,
                $"Profile '{entry.Id}' (for role '{role.Id}') uses harness '{entry.Harness}', which this build cannot run (it runs: {string.Join(", ", Harnesses.Known.Where(Harnesses.IsSupported).Select(h => $"'{h}'"))}). No other harness was used in its place.");
        }

        var problems = factory.ValidateExecution(entry.Id, entry.ToExecution());

        if (problems.Count > 0)
        {
            throw new BindingRefusedException(
                ControllerErrorCodes.UnsupportedConfiguration,
                $"Profile '{entry.Id}' (for role '{role.Id}') cannot be launched as configured: {string.Join("; ", problems)}. Nothing was dropped or defaulted.");
        }

        return new ResolvedRoleBinding(role, entry);
    }

    private static ProfileId? Named(IReadOnlyDictionary<RoleId, ProfileId> requested, RoleId role) => requested.TryGetValue(role, out var profile) ? profile : null;

    private static BindingRefusedException Missing(ResolvedProject project, Role role, string because, string setting)
        => new(
            ControllerErrorCodes.MissingBinding,
            $"{because} needs the '{role.Id}' role bound, and no profile was named for it: the request names none and the project configuration '{project.ConfigPath}' has no '{setting}'. "
            + $"The role is never silently given another role's profile; name one (known: {project.Catalog.KnownIds}) and run again. Nothing was started.");
}

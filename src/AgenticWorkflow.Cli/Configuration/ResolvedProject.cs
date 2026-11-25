using System.Security.Cryptography;
using System.Text.Json;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Runtime.Herdr;

namespace AgenticWorkflow.Cli.Configuration;

/// <summary>
/// Everything a run needs, resolved and validated before any agent exists: the workspace, the strict configuration, and the existing
/// direct-path objects built from it (scope, checks, role/profile). The completion boundary is always
/// <see cref="Core.Lifecycle.CompletionBoundary.WorkingTree"/>; it is not configurable because nothing else is implemented.
/// </summary>
internal sealed record ResolvedProject(
    string Workspace,
    string ConfigPath,
    string ConfigSha256,
    ProjectConfig Config,
    Toolchain Toolchain,
    AgentProfileEntry Profile,
    WorkspaceScope Scope,
    IReadOnlyList<VerificationCheckSpec> Checks,
    AgentProfileEntry? ReviewerProfile = null,
    AgentProfileEntry? ProverProfile = null)
{
    public ProfileId ProfileId => Profile.Id;

    public RoleId RoleId => DirectPathRoles.RootRoleId;

    /// <summary>
    /// Identifies what fixes a run's scope and checks: the configuration without its profiles and the bindings of roles to them (a run records those itself, when its roles are bound).
    /// A change to a profile therefore never stops a run from continuing, while a change to what the run is allowed to do still does.
    /// </summary>
    public string SemanticsSha256 => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        Config.Role,
        Config.Toolchain,
        Config.MaxTurns,
        Config.Verification,
        Config.Writable,
        Config.Protected,
        Config.AllowCommands,
        Config.OperatingNotes,
        Config.Acceptance,
    })));

    /// <summary>The profiles available to this project: the built-in one and those its configuration defines.</summary>
    public ProfileCatalog Catalog => Config.Catalog;

    /// <summary>The project's configured root profile as Core records it in a role binding: what it provides, nothing about how it is launched (that is the binding's execution configuration).</summary>
    public Profile ProfileDefinition => Profile.ToCoreProfile(DirectPathRoles.Root);

    /// <summary>The profile the project binds to the reviewer role, as Core records it: repository tools only, no code editing. Null when the project binds none.</summary>
    public Profile? ReviewerProfileDefinition => ReviewerProfile?.ToCoreProfile(DirectPathRoles.Reviewer);

    /// <summary>The profile the project binds to the prover role, as Core records it: repository tools only, no code editing. Null when the project binds none.</summary>
    public Profile? ProverProfileDefinition => ProverProfile?.ToCoreProfile(DirectPathRoles.Prover);

    /// <summary>The toolchain's pre-approved commands plus the project's narrow additions; the baseline deny list (plus the toolchain's) always wins.</summary>
    public ClaudeCodeToolPolicy ToolPolicy => new()
    {
        AllowedTools = [.. Toolchain.AllowedTools, .. Config.AllowCommands.Select(command => $"Bash({command}:*)")],
        DeniedTools = [.. ClaudeCodeToolPolicy.BaselineDeniedTools, .. Toolchain.AdditionalDeniedTools],
    };

    /// <summary>The toolchain's guidance followed by the project's own notes.</summary>
    public string OperatingNotes => Config.OperatingNotes is { } notes ? $"{Toolchain.OperatingNotes}\n\n{notes.Trim()}" : Toolchain.OperatingNotes;

    /// <summary>The definition of a first stretch under the project's own configured bindings.</summary>
    public DirectTaskDefinition ToDefinition(string objective)
        => new(objective, Workspace, RoleId, ProfileId, Scope, Checks, Config.MaxTurns)
        {
            OperatingNotes = OperatingNotes,
            ReviewerProfile = ReviewerProfileDefinition,
            ProverProfile = ProverProfileDefinition,
        };

    /// <summary>The definition of a first stretch under the bindings that were resolved for the run (the request's choices, else the project's), which the caller is about to record.</summary>
    public DirectTaskDefinition ToDefinition(string objective, IReadOnlyList<ResolvedRoleBinding> bindings)
        => new(objective, Workspace, RoleId, bindings.First(b => b.Role.Id == DirectPathRoles.RootRoleId).Profile.Id, Scope, Checks, Config.MaxTurns)
        {
            OperatingNotes = OperatingNotes,
            ReviewerProfile = bindings.FirstOrDefault(b => b.Role.Id == DirectPathRoles.ReviewerRoleId)?.CoreProfile,
            ProverProfile = bindings.FirstOrDefault(b => b.Role.Id == DirectPathRoles.ProverRoleId)?.CoreProfile,
        };

    /// <summary>
    /// The definition of any later stretch: the profiles are the ones the run recorded when its roles were bound, never the ones the configuration names now. A run whose root role
    /// has no binding cannot be continued; it is refused, and nothing is substituted.
    /// </summary>
    public DirectTaskDefinition ToDefinition(WorkflowTask task)
    {
        if (!task.RoleBindings.TryGetValue(DirectPathRoles.RootRoleId, out var root))
            throw new ConfigurationException($"The run has no binding for its '{DirectPathRoles.RootRoleId}' role, so no agent can be started for it; bind it first (aw bind).");

        return new DirectTaskDefinition(task.Intent, Workspace, RoleId, root.Profile.Id, Scope, Checks, Config.MaxTurns)
        {
            OperatingNotes = OperatingNotes,
            ReviewerProfile = task.RoleBindings.TryGetValue(DirectPathRoles.ReviewerRoleId, out var reviewer) ? reviewer.Profile : null,
            ProverProfile = task.RoleBindings.TryGetValue(DirectPathRoles.ProverRoleId, out var prover) ? prover.Profile : null,
        };
    }
}

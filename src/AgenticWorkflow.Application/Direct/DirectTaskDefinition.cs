using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Application.Direct;

/// <summary>The canonical role the direct path runs under.</summary>
public static class DirectPathRoles
{
    public static RoleId RootRoleId { get; } = new("root");

    public static ProfileCapability CodeEdit { get; } = new("code_edit");

    public static ProfileCapability RepositoryTools { get; } = new("repository_tools");

    public static RoleId ReviewerRoleId => WellKnownRoles.Reviewer;

    /// <summary>
    /// The <c>reviewer</c> role: an independent reader of the verified implementation. It needs only repository tools: it never edits. It is bound explicitly or it is
    /// unbound; it is never implicitly the profile of <see cref="Root"/>.
    /// </summary>
    public static Role Reviewer { get; } = new(
        ReviewerRoleId,
        IdentifierSet<ProfileCapability>.Of(RepositoryTools));

    public static RoleId ProverRoleId => WellKnownRoles.Prover;

    /// <summary>
    /// The <c>prover</c> role: an independent, read-only judge of the Proof Contract over the verified implementation. It needs only repository tools: it never edits. It is
    /// bound explicitly or it is unbound; it is never implicitly the profile of <see cref="Root"/> or <see cref="Reviewer"/>.
    /// </summary>
    public static Role Prover { get; } = new(
        ProverRoleId,
        IdentifierSet<ProfileCapability>.Of(RepositoryTools));

    /// <summary>The <c>root</c> role: the single agent that implements, observes and repairs. Needs code editing and repository tools.</summary>
    public static Role Root { get; } = new(
        RootRoleId,
        IdentifierSet<ProfileCapability>.Of(CodeEdit, RepositoryTools));
}

/// <summary>
/// Which workspace paths the run may change. Paths are relative to the workspace root, '/'-separated; an entry
/// ending in '/' is a directory prefix, anything else is one exact file. Comparison ignores case (Windows).
/// <see cref="ProtectedPaths"/> always win. An empty <see cref="AllowedPaths"/> means "anywhere not protected".
/// This is a consistency sensor over observed changes, not semantic classification.
/// </summary>
public sealed record WorkspaceScope
{
    public WorkspaceScope(IEnumerable<string>? allowedPaths = null, IEnumerable<string>? protectedPaths = null)
    {
        AllowedPaths = (allowedPaths ?? []).Select(Normalize).ToArray();
        ProtectedPaths = (protectedPaths ?? []).Select(Normalize).ToArray();
    }

    public IReadOnlyList<string> AllowedPaths { get; }

    public IReadOnlyList<string> ProtectedPaths { get; }

    /// <summary>The changed paths that are protected, or that are outside a non-empty allow list, with the reason.</summary>
    public IReadOnlyList<ScopeViolation> Evaluate(IEnumerable<string> changedPaths)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);

        var violations = new List<ScopeViolation>();

        foreach (var path in changedPaths.Select(NormalizeObserved).Order(StringComparer.Ordinal))
        {
            if (ProtectedPaths.Any(entry => Matches(entry, path)))
                violations.Add(new ScopeViolation(path, "protected path changed"));
            else if (AllowedPaths.Count > 0 && !AllowedPaths.Any(entry => Matches(entry, path)))
                violations.Add(new ScopeViolation(path, "path is outside the allowed scope"));
        }

        return violations;
    }

    private static bool Matches(string entry, string path)
        => entry.EndsWith('/')
            ? path.StartsWith(entry, StringComparison.OrdinalIgnoreCase)
            : string.Equals(entry, path, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            throw new ArgumentException("A scope entry must not be blank.", nameof(entry));

        var normalized = entry.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];

        if (normalized.StartsWith('/') || normalized.Contains(':') || normalized.Split('/').Contains(".."))
            throw new ArgumentException($"Scope entry '{entry}' must be a relative path inside the workspace.", nameof(entry));

        return normalized;
    }

    private static string NormalizeObserved(string path) => path.Replace('\\', '/');
}

/// <summary>A changed path that the scope does not permit.</summary>
public sealed record ScopeViolation(string Path, string Reason);

/// <summary>
/// What a caller explicitly hands the direct path. The direct path does no composition or classification: the caller has
/// already decided this task needs no human gate, no formal PROVE, no independent REVIEW and no Dynamic
/// Coordinator. Escalating out of the direct path when that turns out to be wrong is not supported.
/// </summary>
public sealed record DirectTaskDefinition
{
    public const int MaxAllowedTurns = 20;

    public DirectTaskDefinition(
        string objective,
        string workspacePath,
        RoleId rootRole,
        ProfileId executionProfile,
        WorkspaceScope scope,
        IReadOnlyList<VerificationCheckSpec> checks,
        int maxAgentTurns)
    {
        ArgumentNullException.ThrowIfNull(rootRole);
        ArgumentNullException.ThrowIfNull(executionProfile);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(checks);

        if (string.IsNullOrWhiteSpace(objective))
            throw new ArgumentException("A direct task needs an objective.", nameof(objective));

        if (string.IsNullOrWhiteSpace(workspacePath))
            throw new ArgumentException("A direct task needs a workspace path.", nameof(workspacePath));

        if (checks.Count == 0)
            throw new ArgumentException("A direct task needs at least one verification check.", nameof(checks));

        if (checks.Select(c => c.Id).Distinct().Count() != checks.Count)
            throw new ArgumentException("Verification check ids must be unique.", nameof(checks));

        if (maxAgentTurns is < 1 or > MaxAllowedTurns)
            throw new ArgumentOutOfRangeException(nameof(maxAgentTurns), maxAgentTurns, $"MaxAgentTurns must be between 1 and {MaxAllowedTurns}.");

        Objective = objective;
        WorkspacePath = workspacePath;
        RootRole = rootRole;
        ExecutionProfile = executionProfile;
        Scope = scope;
        Checks = checks.ToArray();
        MaxAgentTurns = maxAgentTurns;
    }

    public string Objective { get; }

    public string WorkspacePath { get; }

    public RoleId RootRole { get; }

    /// <summary>The profile the caller expects the role to be bound to. It must equal the binding exactly; it is never a fallback.</summary>
    public ProfileId ExecutionProfile { get; }

    public WorkspaceScope Scope { get; }

    public IReadOnlyList<VerificationCheckSpec> Checks { get; }

    public int MaxAgentTurns { get; }

    /// <summary>
    /// Optional host-supplied guidance on how to work within the agent's tool permissions (for example which commands are
    /// pre-approved). Sent verbatim in the first prompt. It carries no authority: permissions are enforced by the runtime profile.
    /// </summary>
    public string? OperatingNotes { get; init; }

    /// <summary>
    /// The profile the project explicitly configured for the Reviewer role, or null when it configured none. It is the only thing that can bind the role: with no
    /// binding and none here an independent review cannot start, and the implementer's profile is never used instead. It may be the very profile the implementer uses, but
    /// only because the project said so for the reviewer role.
    /// </summary>
    public Profile? ReviewerProfile { get; init; }

    /// <summary>
    /// The profile the project explicitly configured for the Prover role, or null when it configured none. Like <see cref="ReviewerProfile"/> it is the only thing that can
    /// bind the role: with no binding and none here a formal proof cannot start, and neither the implementer's nor the reviewer's profile is ever used instead.
    /// </summary>
    public Profile? ProverProfile { get; init; }
}

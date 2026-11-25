using AgenticWorkflow.Core.Gates;

namespace AgenticWorkflow.Cli.Configuration;

/// <summary>One controller verification check as written in the project configuration (see <see cref="ProjectConfigParser"/>).</summary>
internal sealed record CheckConfig(
    string Id,
    string Executable,
    IReadOnlyList<string> Arguments,
    int TimeoutSeconds,
    string? Description);

/// <summary>
/// The syntactically valid content of <c>agenticworkflow.json</c> (version 1). <see cref="Acceptance"/> is the project's default for new runs; a run can override it.
/// <see cref="Profile"/>, <see cref="ReviewerProfile"/> and <see cref="ProverProfile"/> are the project's explicit bindings of the root, reviewer and prover roles, each naming a profile of the catalog (<see cref="Catalog"/>): a start request may bind a role to another profile, and a role the project does not bind is unbound unless the request binds it. The reviewer and prover bindings: absent means the role is unbound (an independent review or proof cannot run), never "the implementer's profile" or each other's. Nothing here is resolved or defaulted beyond what the
/// file format itself defines; <see cref="ProjectResolver"/> turns it into the objects the direct path runs.
/// </summary>
internal sealed record ProjectConfig(
    string Role,
    string Profile,
    string Toolchain,
    int MaxTurns,
    IReadOnlyList<CheckConfig> Verification,
    IReadOnlyList<string> Writable,
    IReadOnlyList<string> Protected,
    IReadOnlyList<string> AllowCommands,
    string? OperatingNotes,
    AcceptanceRequirement Acceptance = AcceptancePolicy.Default,
    string? ReviewerProfile = null,
    string? ProverProfile = null,
    IReadOnlyList<ProfileConfig>? Profiles = null)
{
    /// <summary>The profiles this configuration defines, in the order written (the built-in <c>claude-code-local</c> is not among them).</summary>
    public IReadOnlyList<ProfileConfig> DefinedProfiles { get; } = Profiles ?? [];

    /// <summary>The profiles available to the project: the built-in one and the ones it defines.</summary>
    public ProfileCatalog Catalog => ProfileCatalog.From(DefinedProfiles);

    public const int SupportedVersion = 1;

    public const int DefaultMaxTurns = 3;

    public const int DefaultCheckTimeoutSeconds = 600;

    public const int MaxCheckTimeoutSeconds = 7200;

    public const int MaxOperatingNotesLength = 4000;
}

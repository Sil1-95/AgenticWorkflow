using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Core.Roles;

/// <summary>A responsibility within the workflow (e.g. <c>implementer</c>, <c>reviewer</c>).</summary>
public sealed record RoleId : StableIdentifier
{
    public RoleId(string value)
        : base(value)
    {
    }
}

/// <summary>A concrete execution configuration (e.g. a specific model/harness/settings bundle).</summary>
public sealed record ProfileId : StableIdentifier
{
    public ProfileId(string value)
        : base(value)
    {
    }
}

/// <summary>
/// A provider-neutral, extensible capability (<c>code_edit</c>, <c>repository_tools</c>, <c>fresh_context</c>,
/// <c>read_only</c>, <c>semantic_reasoning</c>, ...). Never an intelligence score and never a provider name.
/// </summary>
public sealed record ProfileCapability : StableIdentifier
{
    public ProfileCapability(string value)
        : base(value)
    {
    }
}

/// <summary>A role and the capabilities any profile bound to it must provide.</summary>
public sealed record Role
{
    public Role(RoleId id, IdentifierSet<ProfileCapability> requiredCapabilities)
    {
        Id = Guard.NotNull(id, nameof(id));
        RequiredCapabilities = Guard.NotNull(requiredCapabilities, nameof(requiredCapabilities));
    }

    public RoleId Id { get; }

    public IdentifierSet<ProfileCapability> RequiredCapabilities { get; }
}

/// <summary>A profile and the capabilities it provides. Minimal by design; no provider settings.</summary>
public sealed record Profile
{
    public Profile(ProfileId id, IdentifierSet<ProfileCapability> capabilities, string? description = null)
    {
        Id = Guard.NotNull(id, nameof(id));
        Capabilities = Guard.NotNull(capabilities, nameof(capabilities));
        Description = description;
    }

    public ProfileId Id { get; }

    public IdentifierSet<ProfileCapability> Capabilities { get; }

    public string? Description { get; }
}

/// <summary>
/// How a bound profile is launched, as it was when the binding was made: the harness that runs it and the model and reasoning it asks that harness for. Model and reasoning are opaque
/// text, never enumerations: only the harness's adapter knows what it supports, and it is asked before the binding exists. A null model or reasoning means the profile did not choose one
/// (the harness's own setting applies); nothing is filled in. <see cref="Fingerprint"/> identifies the profile definition these values came from, so a later change to that definition
/// is recognisable without the definition being reread. Nothing here is secret and nothing is a command.
/// </summary>
public sealed record ExecutionConfiguration
{
    public ExecutionConfiguration(string harness, string? model, string? reasoning, string fingerprint)
    {
        Harness = Guard.NotBlank(harness, nameof(harness));
        Model = model is null ? null : Guard.NotBlank(model, nameof(model));
        Reasoning = reasoning is null ? null : Guard.NotBlank(reasoning, nameof(reasoning));
        Fingerprint = Guard.NotBlank(fingerprint, nameof(fingerprint));
    }

    public string Harness { get; }

    public string? Model { get; }

    public string? Reasoning { get; }

    public string Fingerprint { get; }
}

/// <summary>
/// The user/project decision "this role is performed by this profile", recorded against the semantic revision
/// it was made under. The role and profile definitions are snapshotted so history replays without any
/// registry. Construction fails if the profile lacks a required capability: a binding is never silently
/// weakened or substituted. <see cref="Execution"/> is how the profile is launched, resolved once at binding time and never reread; it is null for a binding recorded before
/// execution configuration existed.
/// </summary>
public sealed record RoleBinding
{
    public RoleBinding(Role role, Profile profile, SemanticRevisionId boundAt, ExecutionConfiguration? execution = null)
    {
        Guard.NotNull(role, nameof(role));
        Guard.NotNull(profile, nameof(profile));

        if (!profile.Capabilities.IsSupersetOf(role.RequiredCapabilities))
        {
            var missing = role.RequiredCapabilities.Where(c => !profile.Capabilities.Contains(c));
            throw new RoleBindingException(
                $"Profile '{profile.Id}' cannot fill role '{role.Id}': missing capabilities {string.Join(", ", missing)}.");
        }

        Role = role;
        Profile = profile;
        BoundAt = boundAt;
        Execution = execution;
    }

    public Role Role { get; }

    public Profile Profile { get; }

    public SemanticRevisionId BoundAt { get; }

    public ExecutionConfiguration? Execution { get; }
}

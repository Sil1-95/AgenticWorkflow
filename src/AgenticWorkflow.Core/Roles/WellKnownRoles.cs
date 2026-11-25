namespace AgenticWorkflow.Core.Roles;

/// <summary>
/// The role names Core itself must be able to refer to. REVIEW needs one because the replay rule that a review came from the explicitly bound reviewer is a Core
/// invariant; which capabilities the role requires and which profile fills it are decisions made outside Core.
/// </summary>
public static class WellKnownRoles
{
    /// <summary>The independent reviewer of a verified implementation. Never implicitly the implementer: it is bound explicitly or it is unbound.</summary>
    public static RoleId Reviewer { get; } = new("reviewer");

    /// <summary>The independent prover of a Proof Contract. Never implicitly the implementer or the reviewer: it is bound explicitly or it is unbound.</summary>
    public static RoleId Prover { get; } = new("prover");
}

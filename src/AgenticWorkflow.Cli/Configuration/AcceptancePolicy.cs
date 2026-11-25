using AgenticWorkflow.Core.Gates;

namespace AgenticWorkflow.Cli.Configuration;

/// <summary>
/// The two words a person uses for the acceptance requirement, in <c>agenticworkflow.json</c> (<c>"acceptance"</c>) and on <c>aw run --acceptance</c>. They map one to one
/// onto Core's <see cref="AcceptanceRequirement"/>; there are no other modes.
/// </summary>
internal static class AcceptancePolicy
{
    public const string None = "none";
    public const string Human = "human";

    /// <summary>What a run uses when neither the project configuration nor the command line says: the lightweight direct path, no final ACCEPT.</summary>
    public const AcceptanceRequirement Default = AcceptanceRequirement.NotRequired;

    public static string KnownWords => $"'{None}', '{Human}'";

    public static AcceptanceRequirement? Parse(string word) => word switch
    {
        None => AcceptanceRequirement.NotRequired,
        Human => AcceptanceRequirement.HumanRequired,
        _ => null,
    };

    /// <summary>The word for a requirement, as it is written in configuration and on the command line.</summary>
    public static string Word(AcceptanceRequirement requirement) => requirement == AcceptanceRequirement.HumanRequired ? Human : None;

    /// <summary>How a person reads the requirement in output (<c>aw run</c>, <c>aw status</c>).</summary>
    public static string Describe(AcceptanceRequirement requirement) => requirement == AcceptanceRequirement.HumanRequired
        ? "required (human ACCEPT)"
        : "not required";
}

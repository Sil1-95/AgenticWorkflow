namespace AgenticWorkflow.Runtime.Herdr.Tests.Live;

/// <summary>
/// A test that needs a real herdr-core installation. Skipped unless the named environment variable is "1",
/// so the normal <c>dotnet test</c> run never touches Herdr.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveFactAttribute : FactAttribute
{
    public const string HerdrVariable = "AGENTICWORKFLOW_LIVE_HERDR";
    public const string ClaudeVariable = "AGENTICWORKFLOW_LIVE_CLAUDE";
    public const string ClaudeSessionVariable = "AGENTICWORKFLOW_LIVE_CLAUDE_SESSION";
    public const string ClaudeEvidenceVariable = "AGENTICWORKFLOW_LIVE_CLAUDE_EVIDENCE";

    public LiveFactAttribute(string variable = HerdrVariable)
    {
        if (Environment.GetEnvironmentVariable(variable) != "1")
            Skip = $"Live Herdr test. Set {variable}=1 to run it.";
    }
}

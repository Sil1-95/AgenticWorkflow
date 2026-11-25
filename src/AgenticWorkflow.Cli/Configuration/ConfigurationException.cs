namespace AgenticWorkflow.Cli.Configuration;

/// <summary>
/// The project (or its environment) cannot be used as configured. Always raised before any agent is launched and always carries
/// every problem found, so a person fixes the file once rather than once per run.
/// </summary>
internal sealed class ConfigurationException : Exception
{
    public ConfigurationException(IReadOnlyList<string> problems)
        : base(string.Join(Environment.NewLine, problems))
    {
        Problems = problems;
    }

    public ConfigurationException(string problem)
        : this([problem])
    {
    }

    public IReadOnlyList<string> Problems { get; }
}

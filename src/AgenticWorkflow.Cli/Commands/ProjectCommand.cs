using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Cli.Reporting;

namespace AgenticWorkflow.Cli.Commands;

/// <summary>
/// <c>aw project inspect</c> and <c>aw project init</c>: the typed project operations an integration (the Herdr cockpit) uses instead of reading or writing project files itself. Both print one
/// JSON document. <c>inspect</c> is read-only. <c>init</c> creates <c>agenticworkflow.json</c> in a workspace from values a person confirmed, validated by the same strict parser <c>aw run</c> uses,
/// and never replaces a file that exists.
/// </summary>
internal sealed class ProjectCommand(CliServices services)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public Task<int> ExecuteAsync(ProjectRequest request, CancellationToken cancellationToken)
        => request switch
        {
            ProjectInspectRequest inspect => Task.FromResult(Inspect(inspect)),
            ProjectInitRequest init => InitAsync(init, cancellationToken),
            _ => throw new InvalidOperationException("Unhandled project request."),
        };

    private int Inspect(ProjectInspectRequest request)
    {
        Write(ProjectInspector.Inspect(request.Workspace, request.Config, services.CurrentDirectory, services.RuntimeFactory));

        return ExitCodes.Success;
    }

    private async Task<int> InitAsync(ProjectInitRequest request, CancellationToken cancellationToken)
    {
        var workspace = Path.GetFullPath(request.Workspace, services.CurrentDirectory).TrimEnd('\\', '/');
        var (bytes, readProblem) = await RequestFile.ConsumeAsync(request.RequestFile, MaxRequestBytes, cancellationToken).ConfigureAwait(false);

        if (bytes is null)
            return Refuse([readProblem!]);

        if (!Directory.Exists(workspace))
            return Refuse([$"The workspace '{workspace}' does not exist or is not a directory."]);

        var (decoded, decodeProblems) = ProjectInitializer.Decode(bytes);

        if (decoded is null)
            return Refuse(decodeProblems);

        var (text, problems) = ProjectInitializer.Build(decoded);

        if (text is null)
            return Refuse(problems);

        var path = Path.Combine(workspace, ProjectResolver.DefaultConfigFileName);

        try
        {
            // CreateNew: a configuration that exists is never replaced, however the race with another writer falls.
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await stream.WriteAsync(new System.Text.UTF8Encoding(false).GetBytes(text), cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) when (File.Exists(path))
        {
            return Refuse([$"'{path}' already exists. It was not changed."]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refuse([$"'{path}' could not be written: {e.Message}"]);
        }

        services.Out.Write(JsonSerializer.Serialize(new InitResult(true, path, []), Json) + "\n");

        return ExitCodes.Success;
    }

    private int Refuse(IReadOnlyList<string> problems)
    {
        services.Out.Write(JsonSerializer.Serialize(new InitResult(false, null, problems), Json) + "\n");

        return ExitCodes.UsageOrConfiguration;
    }

    private void Write(object value) => services.Out.Write(JsonSerializer.Serialize(value, Json) + "\n");

    private const int MaxRequestBytes = 64 * 1024;

    private sealed record InitResult(bool Ok, string? ConfigPath, IReadOnlyList<string> Problems);
}

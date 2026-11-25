using AgenticWorkflow.Application;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.Controller;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.Runtime;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Execution.Local;

namespace AgenticWorkflow.Cli;

/// <summary>
/// Everything the commands touch outside their own logic. <see cref="Production"/> is the one real composition: Git state, the local
/// verification runner, a Herdr-hosted Claude Code runtime. Tests substitute individual parts (the runtime above all) and keep the rest real,
/// so the code under test is the product's own.
/// </summary>
internal sealed class CliServices
{
    public required TextWriter Out { get; init; }

    public required TextWriter Error { get; init; }

    /// <summary>Standard input, for the one command that reads a request from it (<c>aw ctl send</c>). Empty unless composed otherwise.</summary>
    public TextReader Input { get; init; } = TextReader.Null;

    /// <summary>True when both input and output are a terminal: only then is a person assumed to be attending without being told so (<c>--interactive</c>).</summary>
    public required bool IsTerminal { get; init; }

    public required string CurrentDirectory { get; init; }

    public required Func<string, string?> GetEnvironmentVariable { get; init; }

    public required string LocalApplicationData { get; init; }

    public required IAgentRuntimeFactory RuntimeFactory { get; init; }

    public required IVerificationRunner Verifier { get; init; }

    public required IImplementationStateProvider Workspace { get; init; }

    public required IClock Clock { get; init; }

    public required IIdSource Ids { get; init; }

    /// <summary>An optional signal outside the console for a person who is not reading it. Off (a no-op) unless composed otherwise; see <see cref="CliEnvironment.NotifyVariable"/>.</summary>
    public IHumanAttentionNotifier Notifier { get; init; } = NoHumanAttentionNotifier.Instance;

    /// <summary>How <c>aw ctl</c> starts a controller that is not running. Null (the default) starts a detached copy of this program; tests substitute their own.</summary>
    public IControllerLauncher? ControllerLauncher { get; init; }

    /// <summary>
    /// The same services for work the controller runs on nobody's console: nothing is written to a terminal (the run's report is its durable state, which clients read), warnings go
    /// to <paramref name="error"/>, no person is assumed to be at a terminal, and the runtime factory and the working directory (where a relative path of a request is resolved)
    /// may be replaced.
    /// </summary>
    public CliServices ForSupervision(TextWriter error, string? currentDirectory = null, IAgentRuntimeFactory? runtimeFactory = null) => new()
    {
        Out = TextWriter.Null,
        Error = error,
        IsTerminal = false,
        CurrentDirectory = currentDirectory ?? CurrentDirectory,
        GetEnvironmentVariable = GetEnvironmentVariable,
        LocalApplicationData = LocalApplicationData,
        RuntimeFactory = runtimeFactory ?? RuntimeFactory,
        Verifier = Verifier,
        Workspace = Workspace,
        Clock = Clock,
        Ids = Ids,
        Notifier = Notifier,
        ControllerLauncher = ControllerLauncher,
    };

    public static CliServices Production()
    {
        var environment = new Func<string, string?>(Environment.GetEnvironmentVariable);

        return new CliServices
        {
            Out = Console.Out,
            Error = Console.Error,
            Input = Console.In,
            IsTerminal = !Console.IsInputRedirected && !Console.IsOutputRedirected,
            CurrentDirectory = Directory.GetCurrentDirectory(),
            GetEnvironmentVariable = environment,
            LocalApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            RuntimeFactory = new HerdrAgentRuntimeFactory(AppContext.BaseDirectory, new ExecutableLocator(environment)),
            Verifier = new LocalVerificationRunner(),
            Workspace = new GitImplementationStateProvider(),
            Clock = new SystemClock(),
            Ids = new GuidIdSource(),
            Notifier = HumanAttentionNotifiers.Select(environment),
        };
    }
}

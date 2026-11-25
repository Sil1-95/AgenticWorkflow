using System.Diagnostics;
using AgenticWorkflow.Application;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.Runtime;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Execution.Local;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>A scripted <see cref="IAgentRuntime"/>: what each turn does to the workspace and how it ends is decided by the test. No model, no Herdr.</summary>
internal sealed class ScriptedRuntime : IAgentRuntime
{
    public List<AgentSessionRequest> StartRequests { get; } = [];

    public List<string> Prompts { get; } = [];

    /// <summary>What was sent to the independent reviewer sessions, in order; the implementer's prompts stay in <see cref="Prompts"/>.</summary>
    public List<string> ReviewerPrompts { get; } = [];

    /// <summary>Called for each turn of an independent reviewer session, with the workspace path. It is where a test makes the reviewer touch the workspace or fail.</summary>
    public Func<int, string, CancellationToken, Task<AgentTurnOutcome>> ReviewerTurn { get; set; } = (_, _, _) => Task.FromResult(AgentTurnOutcome.Done);

    /// <summary>What was sent to the independent prover sessions, in order.</summary>
    public List<string> ProverPrompts { get; } = [];

    /// <summary>Called for each turn of an independent prover session, with the workspace path. It is where a test makes the prover touch the workspace or fail.</summary>
    public Func<int, string, CancellationToken, Task<AgentTurnOutcome>> ProverTurn { get; set; } = (_, _, _) => Task.FromResult(AgentTurnOutcome.Done);

    public List<AgentSessionId> Stopped { get; } = [];

    /// <summary>Called for turn 1, 2, ... with the workspace path. Return the turn's outcome.</summary>
    public Func<int, string, CancellationToken, Task<AgentTurnOutcome>> Turn { get; set; } = (_, _, _) => Task.FromResult(AgentTurnOutcome.Done);

    /// <summary>When set, starting the session fails like a runtime that never became ready.</summary>
    public Exception? StartFailure { get; set; }

    /// <summary>Called while a session starts, with the request: where a test plays a harness that needs a person before it is ready (it may use <see cref="AgentSessionRequest.StartupInteractions"/>).</summary>
    public Func<AgentSessionRequest, CancellationToken, Task>? OnStart { get; set; }

    private int _turns;
    private int _reviewers;
    private int _provers;

    /// <summary>The scripted id of a reviewer session; an implementer session is always <c>scripted-1</c>.</summary>
    public static bool IsReviewer(AgentSessionId session) => session.Value.StartsWith(ReviewerSessionPrefix, StringComparison.Ordinal);

    public const string ReviewerSessionPrefix = "scripted-review-";

    /// <summary>The scripted id of a prover session.</summary>
    public static bool IsProver(AgentSessionId session) => session.Value.StartsWith(ProverSessionPrefix, StringComparison.Ordinal);

    public const string ProverSessionPrefix = "scripted-prove-";

    /// <summary>An observation that the agent is waiting for a person in its own interface (the session stays alive; nothing here can answer it).</summary>
    public static AgentTurnOutcome Wait(string what = "approve Bash: rm -rf build")
        => Wait(what, new NativeAttachTarget("scripted-core", "scripted-1"));

    /// <summary>As <see cref="Wait(string)"/> with an explicit attach target (null: the runtime does not know where the person answers).</summary>
    public static AgentTurnOutcome Wait(string what, NativeAttachTarget? target)
        => AgentTurnOutcome.AwaitingHuman(new AgentInteraction(AgentInteractionKind.Permission, what, target));

    public async Task<AgentSessionId> StartSessionAsync(AgentSessionRequest request, CancellationToken cancellationToken = default)
    {
        StartRequests.Add(request);

        if (StartFailure is { } failure)
            throw failure;

        if (OnStart is { } onStart)
            await onStart(request, cancellationToken);

        return new AgentSessionId(request.Purpose switch
        {
            AgentSessionPurpose.Review => $"{ReviewerSessionPrefix}{++_reviewers}",
            AgentSessionPurpose.Prove => $"{ProverSessionPrefix}{++_provers}",
            _ => "scripted-1",
        });
    }

    public Task<AgentTurnNumber> SendTurnAsync(AgentSessionId session, string prompt, CancellationToken cancellationToken = default)
    {
        (IsReviewer(session) ? ReviewerPrompts : IsProver(session) ? ProverPrompts : Prompts).Add(prompt);

        return Task.FromResult(new AgentTurnNumber(++_turns));
    }

    public Task<AgentTurnOutcome> WaitForTurnAsync(AgentSessionId session, AgentTurnNumber turn, TimeSpan timeout, CancellationToken cancellationToken = default)
        => (IsReviewer(session) ? ReviewerTurn : IsProver(session) ? ProverTurn : Turn)(turn.Value, StartRequests[0].WorkspacePath, cancellationToken);

    public Task StopSessionAsync(AgentSessionId session, CancellationToken cancellationToken = default)
    {
        Stopped.Add(session);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The optional resume capability of the scripted runtime: the first session's conversation is named after the profile, and a resume attaches a new session
/// to whatever conversation it is given (the test decides what that is).
/// </summary>
internal sealed class ScriptedConversations(ScriptedRuntime runtime) : IAgentConversationRuntime
{
    public const string RuntimeKind = "scripted";

    public const string FirstReference = "scripted-conversation-1";

    public List<(AgentSessionRequest Request, AgentConversationRef Conversation)> Resumes { get; } = [];

    /// <summary>When set, resuming fails with it (a runtime that cannot be reached, or says the conversation does not exist).</summary>
    public Exception? ResumeFailure { get; set; }

    public bool CanResume(ProfileId profile) => true;

    public AgentConversationRef? ConversationOf(AgentSessionId session)
    {
        if (ScriptedRuntime.IsReviewer(session))
        {
            // an independent reviewer is always a conversation of its own, named after its session
            var reviewer = runtime.StartRequests.First(r => r.Purpose == AgentSessionPurpose.Review);
            return new AgentConversationRef(reviewer.Profile, RuntimeKind, $"scripted-conversation-of-{session.Value}");
        }

        if (ScriptedRuntime.IsProver(session))
        {
            // an independent prover is always a conversation of its own, named after its session
            var prover = runtime.StartRequests.First(r => r.Purpose == AgentSessionPurpose.Prove);
            return new AgentConversationRef(prover.Profile, RuntimeKind, $"scripted-conversation-of-{session.Value}");
        }

        return runtime.StartRequests.Count == 0 ? null : new AgentConversationRef(runtime.StartRequests[0].Profile, RuntimeKind, FirstReference);
    }

    public Task<AgentSessionId> ResumeSessionAsync(AgentSessionRequest request, AgentConversationRef conversation, CancellationToken cancellationToken = default)
    {
        Resumes.Add((request, conversation));

        return ResumeFailure is { } failure
            ? Task.FromException<AgentSessionId>(failure)
            : Task.FromResult(new AgentSessionId("scripted-resumed"));
    }
}

/// <summary>
/// The optional signal capability of the scripted runtime: a turn can "raise" a clarification (the test decides which turns), exactly as a runtime would
/// report one for that session and turn.
/// </summary>
internal sealed class ScriptedSignals : IAgentSignalSource
{
    /// <summary>The question reported for the n-th turn collected (1-based across the run's processes), if any.</summary>
    public Dictionary<int, AgentClarificationSignal> ByTurn { get; } = [];

    private int _collected;

    /// <summary>The WHAT proposal reported for the n-th WHAT collection (1-based across the run's processes), if any.</summary>
    public Dictionary<int, AgentWhatSignal> WhatByTurn { get; } = [];

    private int _whatCollected;

    /// <summary>The HOW proposal reported for the n-th HOW collection (1-based across the run's processes), if any.</summary>
    public Dictionary<int, AgentHowSignal> HowByTurn { get; } = [];

    private int _howCollected;

    public Task<IReadOnlyList<AgentHowSignal>> CollectHowProposalsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AgentHowSignal>>(HowByTurn.TryGetValue(++_howCollected, out var signal) ? [signal] : []);

    /// <summary>The reclassification reported for the n-th reclassification collection (1-based across the run's processes), if any.</summary>
    public Dictionary<int, AgentReclassificationSignal> ReclassByTurn { get; } = [];

    private int _reclassCollected;

    public Task<IReadOnlyList<AgentReclassificationSignal>> CollectReclassificationsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AgentReclassificationSignal>>(ReclassByTurn.TryGetValue(++_reclassCollected, out var signal) ? [signal] : []);

    /// <summary>The review report submitted by the n-th reviewer turn collected (1-based across the run's processes), if any.</summary>
    public Dictionary<int, AgentReviewSubmission> ReviewByCall { get; } = [];

    private int _reviewCollected;

    public Task<IReadOnlyList<AgentReviewSubmission>> CollectReviewSubmissionsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AgentReviewSubmission>>(ReviewByCall.TryGetValue(++_reviewCollected, out var submission) ? [submission] : []);

    /// <summary>The review request the agent raised in the n-th implementer turn collected (1-based across the run's processes), if any.</summary>
    public Dictionary<int, AgentReviewRequestSignal> ReviewRequestByTurn { get; } = [];

    private int _reviewRequestCollected;

    public Task<IReadOnlyList<AgentReviewRequestSignal>> CollectReviewRequestsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AgentReviewRequestSignal>>(ReviewRequestByTurn.TryGetValue(++_reviewRequestCollected, out var signal) ? [signal] : []);

    /// <summary>The Proof Contract the agent submitted in the n-th implementer turn collected (1-based across the run's processes), if any.</summary>
    public Dictionary<int, AgentProveRequestSignal> ProveRequestByTurn { get; } = [];

    private int _proveRequestCollected;

    public Task<IReadOnlyList<AgentProveRequestSignal>> CollectProveRequestsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AgentProveRequestSignal>>(ProveRequestByTurn.TryGetValue(++_proveRequestCollected, out var signal) ? [signal] : []);

    /// <summary>The proof submitted by the n-th prover turn collected (1-based across the run's processes), if any.</summary>
    public Dictionary<int, AgentProofSubmission> ProofByCall { get; } = [];

    private int _proofCollected;

    public Task<IReadOnlyList<AgentProofSubmission>> CollectProofSubmissionsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AgentProofSubmission>>(ProofByCall.TryGetValue(++_proofCollected, out var submission) ? [submission] : []);

    public bool CanSignal(ProfileId profile) => true;

    public Task<IReadOnlyList<AgentWhatSignal>> CollectWhatProposalsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AgentWhatSignal>>(WhatByTurn.TryGetValue(++_whatCollected, out var signal) ? [signal] : []);

    public Task<IReadOnlyList<AgentClarificationSignal>> CollectClarificationsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AgentClarificationSignal>>(ByTurn.TryGetValue(++_collected, out var signal) ? [signal] : []);
}

/// <summary>Hands the CLI a <see cref="ScriptedRuntime"/> and records what the CLI created and disposed.</summary>
internal sealed class ScriptedRuntimeFactory : IAgentRuntimeFactory
{
    public ScriptedRuntime Runtime { get; } = new();

    /// <summary>Set to give the runtime the resume capability; null (the default) is a runtime that cannot resume.</summary>
    public ScriptedConversations? Conversations { get; set; }

    /// <summary>Set to give the runtime the signal capability; null (the default) is a runtime the agent cannot ask a question through.</summary>
    public ScriptedSignals? Signals { get; set; }

    public int Created { get; private set; }

    public int Disposed { get; private set; }

    public List<ResolvedProject> Projects { get; } = [];

    /// <summary>Prerequisites to report; empty means everything is present.</summary>
    public List<Prerequisite> Unmet { get; } = [];

    /// <summary>When set, every host this factory creates names this isolated session (as a Herdr-hosted runtime does), so supervision records it.</summary>
    public string? OwnedSession { get; set; }

    /// <summary>The sessions a controller was asked to end because a dead controller left them behind.</summary>
    public List<string> OrphanStops { get; } = [];

    /// <summary>What ending an orphaned session reports: true is "confirmed gone".</summary>
    public bool OrphanStopSucceeds { get; set; } = true;

    public Task<bool> StopOrphanedSessionAsync(string session, CancellationToken cancellationToken)
    {
        OrphanStops.Add(session);

        return Task.FromResult(OrphanStopSucceeds);
    }

    public IReadOnlyList<Prerequisite> CheckPrerequisites(ResolvedProject project)
        => Unmet.Count > 0 ? Unmet : [new Prerequisite("scripted runtime", "in-memory", null)];

    /// <summary>What each runtime was asked to launch, one entry per host created: the run's recorded bindings.</summary>
    public List<RunBindings> Launches { get; } = [];

    /// <summary>What <see cref="ValidateExecution"/> reports for every execution configuration; empty means the scripted runtime honours anything its harness supports.</summary>
    public List<string> ExecutionProblems { get; } = [];

    public IReadOnlyList<string> ValidateExecution(ProfileId profile, ExecutionConfiguration execution)
        => ExecutionProblems.Count > 0
            ? [.. ExecutionProblems]
            : Harnesses.IsSupported(execution.Harness) ? [] : [$"harness '{execution.Harness}' is not supported"];

    public AgentRuntimeHost Create(ResolvedProject project, TaskId runId, RunBindings bindings)
    {
        Created++;
        Projects.Add(project);
        Launches.Add(bindings);

        return new AgentRuntimeHost(Runtime, null, new Owner(this), Conversations, Signals, OwnedSession);
    }

    private sealed class Owner(ScriptedRuntimeFactory factory) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            factory.Disposed++;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// An <see cref="IHumanAttentionNotifier"/> that never shows anything: it records each notice with the console text at that moment, and can be told to
/// report a problem or to throw.
/// </summary>
internal sealed class RecordingNotifier(Func<string> console) : IHumanAttentionNotifier
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<(HumanAttentionNotice Notice, string Console)> Calls { get; } = [];

    /// <summary>Completes when the first notice arrives.</summary>
    public Task Called => _called.Task;

    /// <summary>When set, the notifier reports this as the reason it could not show the notice.</summary>
    public string? Problem { get; set; }

    /// <summary>When set, the notifier throws it.</summary>
    public Exception? Failure { get; set; }

    public Task<string?> NotifyAsync(HumanAttentionNotice notice, CancellationToken cancellationToken)
    {
        lock (_gate)
            Calls.Add((notice, console()));

        _called.TrySetResult();

        if (Failure is { } failure)
            throw failure;

        return Task.FromResult(Problem);
    }
}

/// <summary>
/// A real Git repository plus a disposable state home, wired into the real CLI composition with every part real except the agent runtime:
/// real SQLite event stream, real Git implementation fingerprints, real <see cref="LocalVerificationRunner"/>, real command code.
/// </summary>
internal sealed class CliHarness : IDisposable
{
    public const string Symbol = "AW_SCRIPTED_SYMBOL";

    private readonly string _root;

    public CliHarness(string? config = null)
    {
        Notifier = new RecordingNotifier(() => Out.ToString());
        _root = Path.Combine(Path.GetTempPath(), "aw-cli-tests-" + Guid.NewGuid().ToString("N")[..12]);
        Workspace = Path.Combine(_root, "repo");
        Home = Path.Combine(_root, "home");

        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Home);

        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");

        Write("src/Existing.txt", "existing\n");
        Write("README.md", "fixture\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "initial");

        if (config is not null)
            Write(ProjectResolver.DefaultConfigFileName, config);
    }

    public string Workspace { get; }

    public string Home { get; }

    public StringWriter Out { get; } = new();

    public StringWriter Error { get; } = new();

    /// <summary>What the CLI reads as standard input (only <c>aw ctl send</c> reads it).</summary>
    public TextReader Input { get; set; } = TextReader.Null;

    /// <summary>Whether input and output look like a terminal to the CLI (a person is then assumed to be attending).</summary>
    public bool IsTerminal { get; set; }

    public ScriptedRuntimeFactory Factory { get; } = new();

    /// <summary>The desktop-notification seam: records instead of showing anything.</summary>
    public RecordingNotifier Notifier { get; }

    /// <summary>How <c>aw ctl</c> starts a controller. Null (the default) is the real detached process; controller tests substitute an in-process one.</summary>
    public AgenticWorkflow.Cli.Controller.IControllerLauncher? ControllerLauncher { get; set; }

    /// <summary>Environment seen by the CLI. <c>AGENTICWORKFLOW_HOME</c> points at the disposable home unless a test removes it.</summary>
    public Dictionary<string, string?> Environment { get; } = [];

    /// <summary>
    /// A valid project config. The acceptance policy is stated explicitly (default <c>human</c>: most CLI tests exercise the ACCEPT gate; the product default is
    /// <c>none</c>, covered by <c>AcceptancePolicyCliTests</c>). Pass <c>null</c> to omit the member and get the product default.
    /// </summary>
    public static string ValidConfig(string toolchain = "dotnet", string profile = "claude-code-local", int maxTurns = 3, string extra = "", string? acceptance = "human")
        => $$"""
            {
              "version": 1,
              {{(acceptance is null ? string.Empty : $"\"acceptance\": \"{acceptance}\",")}}
              "role": "root",
              "profile": "{{profile}}",
              "toolchain": "{{toolchain}}",
              "maxTurns": {{maxTurns}},
              {{extra}}
              "verification": [
                { "id": "symbol_present", "executable": "git", "arguments": ["grep", "--untracked", "-q", "-F", "{{Symbol}}", "--", "src"] }
              ],
              "scope": { "writable": ["src/**"], "protected": ["src/locked.txt"] }
            }
            """;

    public CliServices Services(TextWriter? output = null, TextWriter? error = null)
    {
        Environment.TryAdd(CliEnvironment.HomeVariable, Home);

        // The optional timing diagnostic writes to stderr; a developer's own shell setting must not leak into tests that assert on it. A test can still set it.
        Environment.TryAdd(CliEnvironment.TimingVariable, null);

        return new CliServices
        {
            Out = output ?? Out,
            Error = error ?? Error,
            Input = Input,
            IsTerminal = IsTerminal,
            CurrentDirectory = Workspace,
            GetEnvironmentVariable = name => Environment.TryGetValue(name, out var value) ? value : System.Environment.GetEnvironmentVariable(name),
            LocalApplicationData = Path.Combine(_root, "localappdata"),
            RuntimeFactory = Factory,
            Verifier = new LocalVerificationRunner(),
            Workspace = new GitImplementationStateProvider(),
            Clock = new SystemClock(),
            Ids = new GuidIdSource(),
            Notifier = Notifier,
            ControllerLauncher = ControllerLauncher,
        };
    }

    public Task<int> RunAsync(CancellationToken cancellationToken, params string[] args)
        => new CliApp(Services()).RunAsync(args, cancellationToken);

    /// <summary>Runs a command with its own output writers, as a second terminal would, so it can be used while a run is still going.</summary>
    public async Task<(int Exit, string Out, string Error)> RunSeparatelyAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await new CliApp(Services(output, error)).RunAsync(args, CancellationToken.None);

        return (exit, output.ToString(), error.ToString());
    }

    public Task<int> RunAsync(params string[] args) => RunAsync(CancellationToken.None, args);

    /// <summary>The run id the CLI printed first.</summary>
    public TaskId PrintedRunId()
    {
        var line = Out.ToString().Split('\n').First(l => l.StartsWith("Run: ", StringComparison.Ordinal)).Trim();

        return new TaskId(Guid.Parse(line["Run: ".Length..]));
    }

    /// <summary>The gate id the last output named in its "aw accept ... --gate &lt;id&gt;" action line.</summary>
    public GateId PrintedGateId()
    {
        var line = Out.ToString().Split('\n').Last(l => l.Contains("aw accept ", StringComparison.Ordinal) && l.Contains("--gate ", StringComparison.Ordinal)).Trim();

        return new GateId(Guid.Parse(line[(line.IndexOf("--gate ", StringComparison.Ordinal) + "--gate ".Length)..].Trim()));
    }

    /// <summary>The question id the last output named in its "aw answer ... --question &lt;id&gt;" action line.</summary>
    public GateId PrintedQuestionId()
    {
        var line = Out.ToString().Split('\n').Last(l => l.Contains("aw answer ", StringComparison.Ordinal) && l.Contains("--question ", StringComparison.Ordinal)).Trim();
        var rest = line[(line.IndexOf("--question ", StringComparison.Ordinal) + "--question ".Length)..].Trim();

        return new GateId(Guid.Parse(rest.Split(' ')[0]));
    }

    /// <summary>The proposal id the last output named in its "aw approve ... --gate &lt;id&gt;" action line.</summary>
    public GateId PrintedWhatId()
    {
        var line = Out.ToString().Split('\n').Last(l => l.Contains("aw approve ", StringComparison.Ordinal) && l.Contains("--gate ", StringComparison.Ordinal)).Trim();
        var rest = line[(line.IndexOf("--gate ", StringComparison.Ordinal) + "--gate ".Length)..].Trim();

        return new GateId(Guid.Parse(rest.Split(' ')[0]));
    }

    /// <summary>Forgets what was printed so far, as a new process would start with clean output.</summary>
    public void ResetOutput()
    {
        Out.GetStringBuilder().Clear();
        Error.GetStringBuilder().Clear();
    }

    public void Write(string relative, string content)
    {
        var path = Path.Combine(Workspace, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public string Git(params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = Workspace, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return process.ExitCode == 0 ? output : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // temp cleanup is best effort
        }
    }
}

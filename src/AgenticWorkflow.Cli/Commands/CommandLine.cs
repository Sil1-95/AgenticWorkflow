using AgenticWorkflow.Application.Controller;

namespace AgenticWorkflow.Cli.Commands;

/// <summary>The command line is wrong. Always a usage error (exit 2) reported before anything is touched.</summary>
internal sealed class UsageException(string message) : Exception(message);

internal abstract record CliRequest;

internal sealed record HelpRequest(string? Command = null) : CliRequest;

internal sealed record VersionRequest : CliRequest;

/// <summary><c>aw doctor</c>: a read-only diagnosis of the installation and the machine. <see cref="Workspace"/> (null = the current folder) is the project whose profiles are checked.</summary>
internal sealed record DoctorRequest(string? Workspace, bool Json) : CliRequest;

/// <summary>
/// Whether a person is expected to answer the agent's own prompts (its native permission prompt, shown through Herdr) while the run waits.
/// aw never asks or answers them itself.
/// </summary>
internal enum HumanPresence
{
    /// <summary>A person is assumed when both input and output are a terminal; otherwise the run stops when the agent waits for one (exit 15).</summary>
    Auto,

    /// <summary>A person is attending: when the agent waits for one, the run keeps waiting.</summary>
    Interactive,

    /// <summary>Nobody is attending: when the agent waits for a person, the run records it and stops (exit 15).</summary>
    NonInteractive,
}

/// <param name="Acceptance">The per-run <c>--acceptance</c> choice; null means "use the project's default".</param>
/// <param name="RequireWhat">The per-run <c>--require what</c> choice: the human's requirement that a WHAT be approved before anything is implemented.</param>
/// <param name="RequireHow">The per-run <c>--require how</c> choice: the human's requirement that a HOW (implementation strategy) be approved before anything is implemented.</param>
/// <param name="RequireReview">The per-run <c>--require review</c> choice: the human's requirement that a fresh, independent reviewer judge the verified implementation before the task completes or asks for ACCEPT.</param>
/// <param name="RequireProve">The per-run <c>--require prove</c> choice: the human's requirement that a Proof Contract be proven by a fresh, independent prover before the task completes or asks for ACCEPT.</param>
/// <param name="Bindings">The per-run <c>--profile</c>, <c>--reviewer-profile</c> and <c>--prover-profile</c> choices: the profile a role runs under instead of the one the project's configuration binds it to. A role not named here uses the project's binding, if it has one.</param>
internal sealed record RunRequest(
    string? Workspace,
    string? Config,
    string? TaskText,
    string? TaskFile,
    HumanPresence Presence = HumanPresence.Auto,
    AgenticWorkflow.Core.Gates.AcceptanceRequirement? Acceptance = null,
    bool RequireWhat = false,
    bool RequireHow = false,
    bool RequireReview = false,
    bool RequireProve = false,
    IReadOnlyDictionary<AgenticWorkflow.Core.Roles.RoleId, AgenticWorkflow.Core.Roles.ProfileId>? Bindings = null) : CliRequest;

internal sealed record AbortRequest(string RunId) : CliRequest;

/// <summary>An explicit binding of a role the run needs to a profile (<c>aw bind</c>). Both are required: the role is never given a profile that was not named for it.</summary>
internal sealed record BindRequest(string RunId, AgenticWorkflow.Core.Roles.RoleId Role, AgenticWorkflow.Core.Roles.ProfileId Profile) : CliRequest;

/// <summary>
/// An explicit APPROVAL of a WHAT proposal. <see cref="Gate"/> (the proposal's gate id, as printed by <c>aw status</c>) is required: an approval is always
/// about a named proposal, never "whatever is open". It continues the run, so it carries the same presence choice as <c>run</c>.
/// </summary>
internal sealed record ApproveRequest(string RunId, string Gate, HumanPresence Presence = HumanPresence.Auto) : CliRequest;

/// <summary>An explicit ACCEPT of the run's open gate. <see cref="Gate"/> (a gate id) pins the decision to the gate the person reviewed; null means the run's only open gate.</summary>
internal sealed record AcceptRequest(string RunId, string? Gate) : CliRequest;

/// <summary>An explicit REQUEST CHANGES at the run's open gate, with the feedback the agent is to act on. It continues the run, so it carries the same presence choice as <c>run</c>.</summary>
internal sealed record RequestChangesRequest(string RunId, string? Gate, string Feedback, HumanPresence Presence = HumanPresence.Auto) : CliRequest;

/// <summary>
/// An explicit operational retry of the work a human already asked for (<c>aw continue</c>), for a run BLOCKED because the continuation failed. It carries no
/// decision and no feedback: both are already recorded. It continues the run, so it carries the same presence choice as <c>run</c>.
/// </summary>
internal sealed record ContinueRequest(string RunId, HumanPresence Presence = HumanPresence.Auto) : CliRequest;

/// <summary>
/// An explicit ANSWER to a CLARIFY question the agent raised. <see cref="Question"/> (the question's id, as printed by <c>aw status</c>) is required: an answer
/// is always about a named question, never "whatever is open". It continues the run, so it carries the same presence choice as <c>run</c>.
/// </summary>
internal sealed record AnswerRequest(string RunId, string Question, string Text, HumanPresence Presence = HumanPresence.Auto) : CliRequest;

/// <summary><c>aw status</c>. With <see cref="Json"/> the run's structured read model is written instead of the text report.</summary>
internal sealed record StatusRequest(string RunId, bool Json = false) : CliRequest;

internal sealed record ShowRequest(string? Workspace, string? Config) : CliRequest;

/// <summary>A typed project operation (<c>aw project inspect|init</c>) for an integration that must not read or write project files itself.</summary>
internal abstract record ProjectRequest : CliRequest;

/// <summary><c>aw project inspect</c>: what a workspace is configured for and what could be configured, as one JSON document. Read-only.</summary>
internal sealed record ProjectInspectRequest(string? Workspace, string? Config) : ProjectRequest;

/// <summary><c>aw project init</c>: creates the workspace's configuration from the choices in a request file (absolute path, read once and deleted).</summary>
internal sealed record ProjectInitRequest(string Workspace, string RequestFile) : ProjectRequest;

/// <summary><c>aw attach</c>: attaches to the agent's own Herdr session of a run, or (<see cref="Describe"/>) only reports whether there is one.</summary>
internal sealed record AttachRequest(string RunId, bool Describe) : CliRequest;

internal enum CtlAction
{
    Health,
    Tasks,
    Task,
    Stop,
    Serve,

    /// <summary>Sends one typed request, read as JSON from standard input, to the controller (how an integration drives the workflow operations).</summary>
    Send,
}

/// <summary>
/// <c>aw ctl &lt;action&gt;</c>: a typed request to the local controller (started automatically when it is not running), or <c>serve</c>, the controller itself.
/// <see cref="Background"/> (serve only) is how a client starts the controller detached; <see cref="NoStart"/> asks a client not to start one.
/// </summary>
internal sealed record CtlRequest(
    CtlAction Action,
    string? TaskId = null,
    int? Limit = null,
    bool NoStart = false,
    bool Background = false,
    TimeSpan? IdleTimeout = null,
    bool Force = false,
    string? RequestFile = null) : CliRequest;

/// <summary>Lists the most recent runs in the state root (<c>aw runs</c>), newest first, so a run id that was lost can be found again.</summary>
internal sealed record RunsRequest(int Limit) : CliRequest;

/// <summary>
/// A deliberately small, strict parser for <c>run</c>, <c>status</c>, <c>show</c>, <c>ctl</c>, <c>abort</c>, <c>accept</c>, <c>request-changes</c>, <c>answer</c>, <c>continue</c>, <c>--help</c> and <c>--version</c>. Unknown options and extra
/// arguments are errors (never ignored), <c>--name value</c> and <c>--name=value</c> are equivalent, and <c>--</c> ends option parsing so a task
/// may begin with a dash.
/// </summary>
internal static class CommandLine
{
    public const string Run = "run";
    public const string Status = "status";
    public const string Show = "show";
    public const string Runs = "runs";
    public const string Abort = "abort";
    public const string Accept = "accept";
    public const string Approve = "approve";
    public const string RequestChanges = "request-changes";
    public const string Continue = "continue";
    public const string Answer = "answer";
    public const string Ctl = "ctl";
    public const string Bind = "bind";
    public const string Project = "project";
    public const string Attach = "attach";
    public const string Doctor = "doctor";

    /// <summary>The maximum feedback a person can attach to REQUEST CHANGES; recorded in the run's history, so it is bounded.</summary>
    public const int MaxFeedbackLength = AgenticWorkflow.Core.Gates.GateDecision.MaxFeedbackLength;

    /// <summary>The maximum answer a person can give to a CLARIFY question; recorded in the run's history, so it is bounded.</summary>
    public const int MaxAnswerLength = AgenticWorkflow.Core.Gates.GateDecision.MaxAnswerLength;

    /// <summary>How many runs <c>aw runs</c> lists unless told otherwise, and the most it can be asked for.</summary>
    public const int DefaultRunsLimit = 10;

    public const int MaxRunsLimit = 1000;

    /// <summary>Parses the run id argument shared by every command that names a run.</summary>
    public static AgenticWorkflow.Core.Identity.TaskId ParseRunId(string text)
        => Guid.TryParseExact(text, "D", out var guid) && guid != Guid.Empty
            ? new AgenticWorkflow.Core.Identity.TaskId(guid)
            : throw new UsageException($"'{text}' is not a run id (expected a GUID such as 0197d2e1-5c3a-7b8e-9f10-2a3b4c5d6e7f).");

    public static AgenticWorkflow.Core.Identity.GateId? ParseGateId(string? text)
        => text is null
            ? null
            : Guid.TryParseExact(text, "D", out var guid) && guid != Guid.Empty
                ? new AgenticWorkflow.Core.Identity.GateId(guid)
                : throw new UsageException($"'{text}' is not a gate id (expected a GUID, as printed by 'aw status').");

    public static CliRequest Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
            throw new UsageException("No command given.");

        var first = args[0];

        if (first is "--help" or "-h" or "help")
            return new HelpRequest(args.Length > 1 ? args[1] : null);

        if (first is "--version")
            return args.Length == 1 ? new VersionRequest() : throw new UsageException("--version takes no arguments.");

        var rest = args.Skip(1).ToArray();

        return first switch
        {
            Run => ParseRun(rest),
            Status => ParseStatus(rest),
            Show => ParseShow(rest),
            Runs => ParseRuns(rest),
            Abort => ParseAbort(rest),
            Accept => ParseAccept(rest),
            Approve => ParseApprove(rest),
            RequestChanges => ParseRequestChanges(rest),
            Continue => ParseContinue(rest),
            Answer => ParseAnswer(rest),
            Ctl => ParseCtl(rest),
            Bind => ParseBind(rest),
            Project => ParseProject(rest),
            Attach => ParseAttach(rest),
            Doctor => ParseDoctor(rest),
            _ => throw new UsageException($"Unknown command '{first}'."),
        };
    }

    private static CliRequest ParseRun(string[] args)
    {
        var parsed = Options.Read(args, ["interactive", "non-interactive"], "workspace", "config", "task-file", "acceptance", "require", "profile", "reviewer-profile", "prover-profile");

        if (parsed.HelpRequested)
            return new HelpRequest(Run);

        var workspace = parsed.Get("workspace");
        var config = parsed.Get("config");
        var taskFile = parsed.Get("task-file");

        if (parsed.Positionals.Count > 1)
            throw new UsageException("Give the task as one argument; put it in quotes.");

        var text = parsed.Positionals.Count == 1 ? parsed.Positionals[0] : null;

        if (text is not null && taskFile is not null)
            throw new UsageException("Give the task either as text or with --task-file, not both.");

        if (text is null && taskFile is null)
            throw new UsageException("A task is required: aw run \"<task>\" or aw run --task-file <file>.");

        if (text is not null && string.IsNullOrWhiteSpace(text))
            throw new UsageException("The task must not be blank.");

        if (parsed.Has("interactive") && parsed.Has("non-interactive"))
            throw new UsageException("Give either --interactive or --non-interactive, not both.");

        var presence = parsed.Has("interactive") ? HumanPresence.Interactive : parsed.Has("non-interactive") ? HumanPresence.NonInteractive : HumanPresence.Auto;

        AgenticWorkflow.Core.Gates.AcceptanceRequirement? acceptance = null;

        if (parsed.Get("acceptance") is { } word)
        {
            acceptance = AgenticWorkflow.Cli.Configuration.AcceptancePolicy.Parse(word)
                         ?? throw new UsageException($"--acceptance must be one of {AgenticWorkflow.Cli.Configuration.AcceptancePolicy.KnownWords}, not '{word}'.");
        }

        // The stages a person can require are the WHAT, the HOW and the REVIEW, in any combination and any order ("what,how,review"); the closed word list keeps every other
        // stage (PROVE) from being asked for by accident. Requiring one never implies another.
        var (requireWhat, requireHow, requireReview, requireProve) = ParseRequire(parsed.Get("require"));

        var bindings = new Dictionary<AgenticWorkflow.Core.Roles.RoleId, AgenticWorkflow.Core.Roles.ProfileId>();

        AddBinding(bindings, AgenticWorkflow.Application.Direct.DirectPathRoles.RootRoleId, "--profile", parsed.Get("profile"));
        AddBinding(bindings, AgenticWorkflow.Application.Direct.DirectPathRoles.ReviewerRoleId, "--reviewer-profile", parsed.Get("reviewer-profile"));
        AddBinding(bindings, AgenticWorkflow.Application.Direct.DirectPathRoles.ProverRoleId, "--prover-profile", parsed.Get("prover-profile"));

        return new RunRequest(workspace, config, text, taskFile, presence, acceptance, requireWhat, requireHow, requireReview, requireProve, bindings);
    }

    /// <summary>A profile id given on the command line for a role. Malformed text is a usage error here; whether the profile exists is decided against the project's configuration later.</summary>
    private static void AddBinding(Dictionary<AgenticWorkflow.Core.Roles.RoleId, AgenticWorkflow.Core.Roles.ProfileId> bindings, AgenticWorkflow.Core.Roles.RoleId role, string option, string? profile)
    {
        if (profile is null)
            return;

        bindings[role] = ParseProfileId(option, profile);
    }

    private static AgenticWorkflow.Core.Roles.ProfileId ParseProfileId(string option, string text)
    {
        try
        {
            return new AgenticWorkflow.Core.Roles.ProfileId(text);
        }
        catch (ArgumentException)
        {
            throw new UsageException($"{option} must be a profile id (lowercase letters, digits, '_', '.', ':' and '-'), not '{text}'.");
        }
    }

    private static (bool What, bool How, bool Review, bool Prove) ParseRequire(string? value)
    {
        if (value is null)
            return (false, false, false, false);

        var what = false;
        var how = false;
        var review = false;
        var prove = false;

        foreach (var word in value.Split(','))
        {
            switch (word.Trim())
            {
                case "what" when !what:
                    what = true;
                    break;
                case "how" when !how:
                    how = true;
                    break;
                case "review" when !review:
                    review = true;
                    break;
                case "prove" when !prove:
                    prove = true;
                    break;
                case "what" or "how" or "review" or "prove":
                    throw new UsageException($"--require names '{word.Trim()}' more than once.");
                default:
                    throw new UsageException($"--require takes 'what', 'how', 'review' and 'prove', alone or as a comma-separated list (for example 'what,review'), not '{value}'.");
            }
        }

        return (what, how, review, prove);
    }

    private static CliRequest ParseDoctor(string[] args)
    {
        var parsed = Options.Read(args, ["json"], "workspace");

        if (parsed.HelpRequested)
            return new HelpRequest(Doctor);

        return parsed.Positionals.Count == 0
            ? new DoctorRequest(parsed.Get("workspace"), parsed.Has("json"))
            : throw new UsageException($"'doctor' takes no arguments, not '{parsed.Positionals[0]}'.");
    }

    private static CliRequest ParseShow(string[] args)
    {
        var parsed = Options.Read(args, [], "workspace", "config");

        if (parsed.HelpRequested)
            return new HelpRequest(Show);

        return parsed.Positionals.Count == 0
            ? new ShowRequest(parsed.Get("workspace"), parsed.Get("config"))
            : throw new UsageException($"'show' takes no arguments, not '{parsed.Positionals[0]}'.");
    }

    private static CliRequest ParseRuns(string[] args)
    {
        var parsed = Options.Read(args, [], "limit");

        if (parsed.HelpRequested)
            return new HelpRequest(Runs);

        if (parsed.Positionals.Count != 0)
            throw new UsageException($"'runs' takes no arguments, not '{parsed.Positionals[0]}'.");

        if (parsed.Get("limit") is not { } text)
            return new RunsRequest(DefaultRunsLimit);

        return int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var limit) && limit is >= 1 and <= MaxRunsLimit
            ? new RunsRequest(limit)
            : throw new UsageException($"--limit must be a whole number from 1 to {MaxRunsLimit}, not '{text}'.");
    }

    private static CliRequest ParseCtl(string[] args)
    {
        const string usage = "Usage: aw ctl health|tasks [--limit N]|task <task-id>|stop [--force] [--no-start]|send [--request-file <absolute path>]  (or: aw ctl serve [--idle-timeout SECONDS])";

        if (args.Length == 0)
            throw new UsageException(usage);

        var rest = args.Skip(1).ToArray();

        switch (args[0])
        {
            case "health":
            case "tasks":
            case "task":
            case "stop":
            case "send":
            {
                var parsed = Options.Read(rest, ["no-start", "force"], "limit", "request-file");

                if (parsed.HelpRequested)
                    return new HelpRequest(Ctl);

                var action = args[0] switch { "health" => CtlAction.Health, "tasks" => CtlAction.Tasks, "task" => CtlAction.Task, "send" => CtlAction.Send, _ => CtlAction.Stop };
                var expected = action == CtlAction.Task ? 1 : 0;

                if (parsed.Positionals.Count != expected)
                    throw new UsageException(usage);

                if (parsed.Get("limit") is not null && action != CtlAction.Tasks)
                    throw new UsageException("--limit applies to 'ctl tasks' only.");

                int? limit = null;

                if (parsed.Get("limit") is { } text)
                {
                    limit = int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= ControllerProtocol.MaxListLimit
                        ? value
                        : throw new UsageException($"--limit must be a whole number from 1 to {ControllerProtocol.MaxListLimit}, not '{text}'.");
                }

                if (parsed.Has("force") && action != CtlAction.Stop)
                    throw new UsageException("--force applies to 'ctl stop' only.");

                var requestFile = parsed.Get("request-file");

                if (requestFile is not null && action != CtlAction.Send)
                    throw new UsageException("--request-file applies to 'ctl send' only.");

                if (requestFile is not null && !Path.IsPathFullyQualified(requestFile))
                    throw new UsageException("--request-file must be an absolute path.");

                return new CtlRequest(action, action == CtlAction.Task ? parsed.Positionals[0] : null, limit, NoStart: parsed.Has("no-start"), Force: parsed.Has("force"), RequestFile: requestFile);
            }

            case "serve":
            {
                var parsed = Options.Read(rest, ["background"], "idle-timeout");

                if (parsed.HelpRequested)
                    return new HelpRequest(Ctl);

                if (parsed.Positionals.Count != 0)
                    throw new UsageException(usage);

                TimeSpan? idle = null;

                if (parsed.Get("idle-timeout") is { } text)
                {
                    idle = int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds <= 7 * 24 * 3600
                        ? TimeSpan.FromSeconds(seconds)
                        : throw new UsageException($"--idle-timeout must be a whole number of seconds (0 = never), not '{text}'.");
                }

                return new CtlRequest(CtlAction.Serve, Background: parsed.Has("background"), IdleTimeout: idle);
            }

            case "--help" or "-h":
                return new HelpRequest(Ctl);

            default:
                throw new UsageException(usage);
        }
    }

    private static CliRequest ParseProject(string[] args)
    {
        const string usage = "Usage: aw project inspect [--workspace <dir>] [--config <file>]  |  aw project init --workspace <dir> --request-file <absolute path>";

        if (args.Length == 0)
            throw new UsageException(usage);

        if (args[0] is "--help" or "-h")
            return new HelpRequest(Project);

        var rest = args.Skip(1).ToArray();

        switch (args[0])
        {
            case "inspect":
            {
                var parsed = Options.Read(rest, [], "workspace", "config");

                if (parsed.HelpRequested)
                    return new HelpRequest(Project);

                return parsed.Positionals.Count == 0 ? new ProjectInspectRequest(parsed.Get("workspace"), parsed.Get("config")) : throw new UsageException(usage);
            }

            case "init":
            {
                var parsed = Options.Read(rest, [], "workspace", "request-file");

                if (parsed.HelpRequested)
                    return new HelpRequest(Project);

                if (parsed.Positionals.Count != 0 || parsed.Get("workspace") is not { } workspace || parsed.Get("request-file") is not { } file)
                    throw new UsageException(usage);

                return Path.IsPathFullyQualified(file) ? new ProjectInitRequest(workspace, file) : throw new UsageException("--request-file must be an absolute path.");
            }

            default:
                throw new UsageException(usage);
        }
    }

    private static CliRequest ParseAttach(string[] args)
    {
        var parsed = Options.Read(args, ["describe"]);

        if (parsed.HelpRequested)
            return new HelpRequest(Attach);

        if (parsed.Positionals.Count != 1)
            throw new UsageException("Usage: aw attach <run-id> [--describe]");

        ParseRunId(parsed.Positionals[0]);

        return new AttachRequest(parsed.Positionals[0], parsed.Has("describe"));
    }

    /// <summary>
    /// <c>aw bind &lt;run-id&gt; --role &lt;role&gt; --profile &lt;profile&gt;</c>: binds a role the run needs and has no binding for (the reviewer or prover an agent asked for mid-run) to a profile the
    /// project defines. Both options are required: a role never gets a profile that was not named.
    /// </summary>
    private static CliRequest ParseBind(string[] args)
    {
        var parsed = Options.Read(args, [], "role", "profile");

        if (parsed.HelpRequested)
            return new HelpRequest(Bind);

        if (parsed.Positionals.Count != 1 || parsed.Get("role") is not { } role || parsed.Get("profile") is not { } profile)
            throw new UsageException("Usage: aw bind <run-id> --role <root|reviewer|prover> --profile <profile-id>");

        ParseRunId(parsed.Positionals[0]);

        if (AgenticWorkflow.Application.Controller.BindableRoles.Find(role) is null)
            throw new UsageException($"--role must be one of {string.Join(", ", AgenticWorkflow.Application.Controller.BindableRoles.All)}, not '{role}'.");

        return new BindRequest(parsed.Positionals[0], new AgenticWorkflow.Core.Roles.RoleId(role), ParseProfileId("--profile", profile));
    }

    private static CliRequest ParseAbort(string[] args)
    {
        var parsed = Options.Read(args, []);

        if (parsed.HelpRequested)
            return new HelpRequest(Abort);

        return parsed.Positionals.Count == 1
            ? new AbortRequest(parsed.Positionals[0])
            : throw new UsageException("Usage: aw abort <run-id>");
    }

    /// <summary>
    /// <c>aw accept &lt;run-id&gt; [--gate &lt;gate-id&gt;]</c>. The decision is the command itself: anything else on the line (a sentence, "yes", "ship it") is an
    /// unknown argument and a usage error, so no text can ever read as approval.
    /// </summary>
    private static CliRequest ParseAccept(string[] args)
    {
        var parsed = Options.Read(args, [], "gate");

        if (parsed.HelpRequested)
            return new HelpRequest(Accept);

        if (parsed.Positionals.Count != 1)
            throw new UsageException("Usage: aw accept <run-id> [--gate <gate-id>]");

        ParseGateId(parsed.Get("gate"));

        return new AcceptRequest(parsed.Positionals[0], parsed.Get("gate"));
    }

    /// <summary>
    /// <c>aw approve &lt;run-id&gt; --gate &lt;gate-id&gt; [--interactive | --non-interactive]</c>. The decision is the command itself and the gate id is required, so
    /// nothing else on the line (a sentence, "yes", "ship it") can read as approval, and no approval can apply to a proposal other than the one named.
    /// </summary>
    private static CliRequest ParseApprove(string[] args)
    {
        var parsed = Options.Read(args, ["interactive", "non-interactive"], "gate");

        if (parsed.HelpRequested)
            return new HelpRequest(Approve);

        if (parsed.Positionals.Count != 1)
            throw new UsageException("Usage: aw approve <run-id> --gate <gate-id> [--interactive | --non-interactive]");

        var gate = parsed.Get("gate") ?? throw new UsageException("--gate <gate-id> is required: an approval is always about a named proposal (the id is printed by 'aw status').");

        if (parsed.Has("interactive") && parsed.Has("non-interactive"))
            throw new UsageException("Give either --interactive or --non-interactive, not both.");

        ParseGateId(gate);

        var presence = parsed.Has("interactive") ? HumanPresence.Interactive : parsed.Has("non-interactive") ? HumanPresence.NonInteractive : HumanPresence.Auto;

        return new ApproveRequest(parsed.Positionals[0], gate, presence);
    }

    private static CliRequest ParseRequestChanges(string[] args)
    {
        var parsed = Options.Read(args, ["interactive", "non-interactive"], "gate", "feedback");

        if (parsed.HelpRequested)
            return new HelpRequest(RequestChanges);

        if (parsed.Positionals.Count != 1)
            throw new UsageException("Usage: aw request-changes <run-id> --feedback \"<what to change>\" [--gate <gate-id>] [--interactive | --non-interactive]");

        var feedback = parsed.Get("feedback") ?? throw new UsageException("--feedback \"<what to change>\" is required: requesting changes without saying which changes is not a request.");

        if (feedback.Length > MaxFeedbackLength)
            throw new UsageException($"The feedback is {feedback.Length} characters; at most {MaxFeedbackLength} are recorded. Say it more briefly.");

        if (parsed.Has("interactive") && parsed.Has("non-interactive"))
            throw new UsageException("Give either --interactive or --non-interactive, not both.");

        ParseGateId(parsed.Get("gate"));

        var presence = parsed.Has("interactive") ? HumanPresence.Interactive : parsed.Has("non-interactive") ? HumanPresence.NonInteractive : HumanPresence.Auto;

        return new RequestChangesRequest(parsed.Positionals[0], parsed.Get("gate"), feedback.Trim(), presence);
    }

    /// <summary>
    /// <c>aw answer &lt;run-id&gt; --question &lt;question-id&gt; --text "..."</c>. The question id is required and the text is the answer itself (free text is valid
    /// here because it answers a named question; it is never read as an approval of anything).
    /// </summary>
    private static CliRequest ParseAnswer(string[] args)
    {
        var parsed = Options.Read(args, ["interactive", "non-interactive"], "question", "text");

        if (parsed.HelpRequested)
            return new HelpRequest(Answer);

        if (parsed.Positionals.Count != 1)
            throw new UsageException("Usage: aw answer <run-id> --question <question-id> --text \"<your answer>\" [--interactive | --non-interactive]");

        var question = parsed.Get("question") ?? throw new UsageException("--question <question-id> is required: an answer is always about a named question (the id is printed by 'aw status').");
        var text = parsed.Get("text") ?? throw new UsageException("--text \"<your answer>\" is required.");

        if (string.IsNullOrWhiteSpace(text))
            throw new UsageException("--text must not be blank.");

        if (text.Trim().Length > MaxAnswerLength)
            throw new UsageException($"The answer is {text.Trim().Length} characters; at most {MaxAnswerLength} are recorded. Say it more briefly.");

        if (parsed.Has("interactive") && parsed.Has("non-interactive"))
            throw new UsageException("Give either --interactive or --non-interactive, not both.");

        ParseGateId(question);

        var presence = parsed.Has("interactive") ? HumanPresence.Interactive : parsed.Has("non-interactive") ? HumanPresence.NonInteractive : HumanPresence.Auto;

        return new AnswerRequest(parsed.Positionals[0], question, text.Trim(), presence);
    }

    /// <summary>
    /// <c>aw continue &lt;run-id&gt; [--interactive | --non-interactive]</c>. Deliberately has no <c>--feedback</c> and no <c>--gate</c>: it decides nothing, it retries
    /// the work a recorded decision already asks for, so nothing on the line can change what that work is.
    /// </summary>
    private static CliRequest ParseContinue(string[] args)
    {
        var parsed = Options.Read(args, ["interactive", "non-interactive"]);

        if (parsed.HelpRequested)
            return new HelpRequest(Continue);

        if (parsed.Positionals.Count != 1)
            throw new UsageException("Usage: aw continue <run-id> [--interactive | --non-interactive]");

        if (parsed.Has("interactive") && parsed.Has("non-interactive"))
            throw new UsageException("Give either --interactive or --non-interactive, not both.");

        var presence = parsed.Has("interactive") ? HumanPresence.Interactive : parsed.Has("non-interactive") ? HumanPresence.NonInteractive : HumanPresence.Auto;

        return new ContinueRequest(parsed.Positionals[0], presence);
    }

    private static CliRequest ParseStatus(string[] args)
    {
        var parsed = Options.Read(args, ["json"]);

        if (parsed.HelpRequested)
            return new HelpRequest(Status);

        return parsed.Positionals.Count == 1
            ? new StatusRequest(parsed.Positionals[0], parsed.Has("json"))
            : throw new UsageException("Usage: aw status <run-id> [--json]");
    }

    private sealed class Options
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

        public List<string> Positionals { get; } = [];

        public bool HelpRequested { get; private set; }

        public string? Get(string name) => _values.GetValueOrDefault(name);

        public bool Has(string flag) => _flags.Contains(flag);

        /// <param name="flags">Options that take no value (<c>--name</c> only).</param>
        /// <param name="known">Options that take a value.</param>
        public static Options Read(string[] args, string[] flags, params string[] known)
        {
            var options = new Options();
            var optionsEnded = false;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                if (optionsEnded || !arg.StartsWith('-') || arg == "-")
                {
                    options.Positionals.Add(arg);
                    continue;
                }

                if (arg == "--")
                {
                    optionsEnded = true;
                    continue;
                }

                if (arg is "--help" or "-h")
                {
                    options.HelpRequested = true;
                    continue;
                }

                if (!arg.StartsWith("--", StringComparison.Ordinal))
                    throw new UsageException($"Unknown option '{arg}'.");

                var separator = arg.IndexOf('=');
                var name = separator < 0 ? arg[2..] : arg[2..separator];

                if (flags.Contains(name, StringComparer.Ordinal))
                {
                    if (separator >= 0)
                        throw new UsageException($"Option '--{name}' takes no value.");

                    if (!options._flags.Add(name))
                        throw new UsageException($"Option '--{name}' was given more than once.");

                    continue;
                }

                if (!known.Contains(name, StringComparer.Ordinal))
                    throw new UsageException($"Unknown option '--{name}'.");

                string value;

                if (separator >= 0)
                {
                    value = arg[(separator + 1)..];
                }
                else if (i + 1 < args.Length)
                {
                    value = args[++i];
                }
                else
                {
                    throw new UsageException($"Option '--{name}' needs a value.");
                }

                if (string.IsNullOrWhiteSpace(value))
                    throw new UsageException($"Option '--{name}' needs a non-blank value.");

                if (!options._values.TryAdd(name, value))
                    throw new UsageException($"Option '--{name}' was given more than once.");
            }

            return options;
        }
    }
}

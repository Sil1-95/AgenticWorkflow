using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Evidence;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Runtime.Herdr;
using AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;
using AgenticWorkflow.Runtime.Herdr.Pi;

namespace AgenticWorkflow.Cli.Runtime;

/// <summary>One thing the run needs from the machine, and where it was found (or why it was not).</summary>
internal sealed record Prerequisite(string Name, string? Location, string? Problem)
{
    public bool IsMet => Problem is null;
}

/// <summary>
/// The runtime a run owns: the agent port, the optional evidence capability of the same runtime, and whatever must be torn down when the
/// run ends. Disposing it stops everything the run started and nothing else.
/// </summary>
internal sealed class AgentRuntimeHost(
    IAgentRuntime runtime,
    IExecutionEvidenceSource? evidence,
    IAsyncDisposable? owned = null,
    IAgentConversationRuntime? conversations = null,
    IAgentSignalSource? signals = null,
    string? ownedSession = null) : IAsyncDisposable
{
    public IAgentRuntime Runtime { get; } = runtime;

    /// <summary>Null when the runtime cannot observe the agent's own executions: the controller then runs every check.</summary>
    public IExecutionEvidenceSource? Evidence { get; } = evidence;

    /// <summary>Null when the runtime cannot resume a native conversation: a continuation is then an explicit fresh session.</summary>
    public IAgentConversationRuntime? Conversations { get; } = conversations;

    /// <summary>Null when the runtime cannot receive the agent's workflow signals: the agent is then not told it may ask a question, and none can be raised.</summary>
    public IAgentSignalSource? Signals { get; } = signals;

    /// <summary>
    /// The name of the isolated session this host will start, when its runtime is hosted in one that outlives the process that started it (a Herdr core). It is known before
    /// anything is started, so a supervisor can record it durably first and a later process can end it if this one dies. Null when the runtime leaves nothing behind.
    /// </summary>
    public string? OwnedSession { get; } = ownedSession;

    public ValueTask DisposeAsync() => owned?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>
/// How the CLI obtains the agent runtime for a project. The production implementation is <see cref="HerdrAgentRuntimeFactory"/>; the seam
/// exists so a model-free test can drive the real CLI with a scripted runtime. It is not a second execution path: everything after this
/// call (persistence, the runner, verification, output) is the product's own.
/// </summary>
internal interface IAgentRuntimeFactory
{
    /// <summary>What must be present for a run to start (executables, the evidence hook). Never starts an agent or calls a model.</summary>
    IReadOnlyList<Prerequisite> CheckPrerequisites(ResolvedProject project);

    /// <summary>
    /// What a harness cannot honour of an execution configuration (empty when it can run it exactly as written). A profile is bound only after this says it can be launched, and what
    /// it reports is never worked around: nothing is dropped, defaulted or replaced. The default is the harness check alone; an adapter that knows more (the options its program
    /// accepts) says more.
    /// </summary>
    IReadOnlyList<string> ValidateExecution(ProfileId profile, ExecutionConfiguration execution)
        => Harnesses.IsSupported(execution.Harness) ? [] : [$"harness '{execution.Harness}' is not supported"];

    /// <summary>
    /// Creates the runtime for one run, launching each role as <paramref name="bindings"/> (the run's own recorded bindings) say: the harness, model and reasoning of the profile the
    /// role was bound to. Nothing is started until the first session is requested.
    /// </summary>
    AgentRuntimeHost Create(ResolvedProject project, TaskId runId, RunBindings bindings);

    /// <summary>
    /// Ends an isolated session an earlier process created (<see cref="AgentRuntimeHost.OwnedSession"/>) and did not get to stop, and says whether it is confirmed gone. A runtime
    /// that cannot confirm it says false, which keeps its run from being resumed alongside a possibly live agent.
    /// </summary>
    Task<bool> StopOrphanedSessionAsync(string session, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>
/// Composes <see cref="HerdrAgentRuntime"/> for the native harnesses a run's recorded bindings name (Claude Code, Pi): one isolated Herdr core owned by this run (never the user's
/// default core), the profile of each bound role with the toolchain's tool policy, and the evidence hook shipped beside the executable. The harness of a role is whatever its binding
/// recorded: there is no mapping from one harness to another and no fallback.
/// </summary>
internal sealed class HerdrAgentRuntimeFactory(
    string installDirectory,
    ExecutableLocator locator,
    Func<ClaudeCliCapabilities?>? capabilities = null,
    Func<PiCliCapabilities?>? piCapabilities = null,
    Func<IReadOnlyList<string>?>? piProgram = null) : IAgentRuntimeFactory
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly Lazy<ClaudeCliCapabilities?> _capabilities = new(capabilities ?? (() => locator.Find(AgentExecutable) is { } path ? ClaudeCliCapabilities.Probe(path, ProbeTimeout) : null));

    private readonly Lazy<IReadOnlyList<string>?> _piProgram = new(piProgram ?? (() => PiProgram.Resolve(locator, out _)));

    private readonly Lazy<PiCliCapabilities?> _piCapabilities = new(piCapabilities ?? (() => PiProgram.Resolve(locator, out _) is { } program ? PiCliCapabilities.Probe(program, ProbeTimeout) : null));

    public const string HookFileName = "AgenticWorkflow.ClaudeEvidenceHook.exe";

    private const string AgentExecutable = "claude";
    private const string HerdrExecutable = "hc";

    /// <summary>The hook, resolved against the CLI's own install directory: a built or published distribution, never a source tree.</summary>
    public string HookPath => Path.Combine(installDirectory, HookFileName);

    public IReadOnlyList<Prerequisite> CheckPrerequisites(ResolvedProject project)
    {
        Prerequisite Executable(string name, string role)
            => locator.Find(name) is { } path
                ? new Prerequisite(role, path, null)
                : new Prerequisite(role, null, $"'{name}' was not found on PATH");

        var harnesses = new[] { project.Profile, project.ReviewerProfile, project.ProverProfile }.OfType<AgentProfileEntry>().Select(profile => profile.Harness).ToHashSet(StringComparer.Ordinal);
        var prerequisites = new List<Prerequisite> { Executable(HerdrExecutable, "herdr (hc)") };

        // Only the programs of the harnesses the project's own bindings name are needed to start; a profile named later on the command line is checked when it is bound.
        if (harnesses.Contains(Harnesses.ClaudeCode) || harnesses.Count == 0)
            prerequisites.Add(Executable(AgentExecutable, "agent (claude)"));

        if (harnesses.Contains(Harnesses.Pi))
        {
            prerequisites.Add(_piProgram.Value is { } program
                ? new Prerequisite("agent (pi)", program[^1], null)
                : new Prerequisite("agent (pi)", null, PiProgram.Resolve(locator, out var problem) is null ? problem : "Pi cannot be launched directly"));
        }

        prerequisites.Add(File.Exists(HookPath)
            ? new Prerequisite("evidence hook", HookPath, null)
            : new Prerequisite("evidence hook", null, $"'{HookPath}' is missing; the hook must be installed next to aw"));

        return prerequisites;
    }

    public IReadOnlyList<string> ValidateExecution(ProfileId profile, ExecutionConfiguration execution)
    {
        if (string.Equals(execution.Harness, Harnesses.Pi, StringComparison.Ordinal))
            return ValidatePi(execution);

        if (!string.Equals(execution.Harness, Harnesses.ClaudeCode, StringComparison.Ordinal))
            return [$"harness '{execution.Harness}' is not supported"];

        // A profile that chooses neither a model nor a reasoning level needs nothing from the program: it is launched as it always was.
        if (execution.Model is null && execution.Reasoning is null)
            return [];

        if (_capabilities.Value is not { } installed)
            return ["the installed Claude Code could not be asked what it supports ('claude --help' did not answer), so its model and reasoning cannot be checked and are not guessed"];

        var problems = new List<string>();

        if (execution.Model is not null && !installed.SupportsModel)
            problems.Add($"the installed Claude Code has no model option, so model '{execution.Model}' cannot be honoured");

        if (execution.Reasoning is { } reasoning)
        {
            if (installed.EffortLevels is not { } levels)
                problems.Add($"the installed Claude Code has no reasoning option, so reasoning '{reasoning}' cannot be honoured");
            else if (!levels.Contains(reasoning, StringComparer.Ordinal))
                problems.Add($"reasoning '{reasoning}' is not one of the levels the installed Claude Code accepts ({string.Join(", ", levels)})");
        }

        return problems;
    }

    /// <summary>
    /// What the installed Pi cannot honour. Unlike Claude Code, Pi is always asked: whether it can be launched at all (its program, its options) is part of being usable, and a model or
    /// level it does not list is something Pi would only warn about and replace, so it is refused here instead.
    /// </summary>
    private IReadOnlyList<string> ValidatePi(ExecutionConfiguration execution)
    {
        if (_piProgram.Value is null || _piCapabilities.Value is not { } installed)
            return ["the installed Pi could not be found or asked what it supports ('pi --help' and 'pi --list-models' did not answer), so it cannot be launched and its model and reasoning are not guessed"];

        var problems = new List<string>();

        if (installed.MissingOptions.Count > 0)
            problems.Add($"the installed Pi does not offer {string.Join(", ", installed.MissingOptions)}, which AgenticWorkflow needs to launch and resume it");

        bool? modelReasons = null;

        if (execution.Model is { } model)
        {
            if (!model.Contains('/'))
                problems.Add($"model '{model}' is not provider-qualified; Pi models are written 'provider/id' exactly as 'pi --list-models' shows them");
            else if (!installed.Models.TryGetValue(model, out var reasons))
                problems.Add($"model '{model}' is not one of the models the installed Pi lists (a reasoning suffix such as ':high' is not part of a model; set the reasoning separately)");
            else
                modelReasons = reasons;
        }

        if (execution.Reasoning is { } reasoning)
        {
            if (installed.ThinkingLevels is not { } levels)
                problems.Add($"the installed Pi lists no reasoning levels, so reasoning '{reasoning}' cannot be honoured");
            else if (!levels.Contains(reasoning, StringComparer.Ordinal))
                problems.Add($"reasoning '{reasoning}' is not one of the levels the installed Pi accepts ({string.Join(", ", levels)})");
            else if (modelReasons == false && !string.Equals(reasoning, "off", StringComparison.Ordinal))
                problems.Add($"model '{execution.Model}' does not support reasoning, so reasoning '{reasoning}' cannot be honoured (only 'off')");
        }

        return problems;
    }

    public AgentRuntimeHost Create(ResolvedProject project, TaskId runId, RunBindings bindings)
    {
        // One launch configuration per bound role, from what the run recorded for that role. The harness, model and reasoning come from the binding alone: this method never chooses
        // them, and the role only decides what the session is for (its tool policy and the one workflow capability it offers), never which harness runs it.
        var profiles = new List<AgentLaunchProfile>();

        foreach (var binding in bindings.All)
        {
            var isPi = string.Equals(binding.Execution.Harness, Harnesses.Pi, StringComparison.Ordinal);

            if (!isPi && !string.Equals(binding.Execution.Harness, Harnesses.ClaudeCode, StringComparison.Ordinal))
                throw new ConfigurationException($"The run's '{binding.Role}' role is bound to profile '{binding.Profile}', whose harness '{binding.Execution.Harness}' this build cannot run. Nothing was started.");

            var (policy, evidence, purpose) = binding.Role.Value switch
            {
                var role when role == DirectPathRoles.ReviewerRoleId.Value => (
                    project.ToolPolicy.ForReviewer(),
                    new ClaudeEvidenceOptions { HookExecutable = HookPath, OfferReview = true },
                    AgentSessionPurpose.Review),

                var role when role == DirectPathRoles.ProverRoleId.Value => (
                    project.ToolPolicy.ForProver(),
                    new ClaudeEvidenceOptions { HookExecutable = HookPath, OfferProve = true },
                    AgentSessionPurpose.Prove),

                _ => (
                    project.ToolPolicy,
                    new ClaudeEvidenceOptions { HookExecutable = HookPath, OfferClarification = true, OfferWhat = true, OfferHow = true, OfferReclassification = true, OfferReviewRequest = true, OfferProveRequest = true },
                    AgentSessionPurpose.Implementation),
            };

            // The same workflow profile may back several roles; launch configurations are keyed by (profile, purpose). A reviewer or prover bound to the implementer's own profile is still
            // a separate, read-only session with its own capability.
            if (isPi)
            {
                var program = _piProgram.Value
                    ?? throw new ConfigurationException($"The run's '{binding.Role}' role is bound to profile '{binding.Profile}' (harness 'pi'), and Pi cannot be launched here: {(PiProgram.Resolve(locator, out var problem) is null ? problem : "its program could not be resolved")}. Nothing was started.");

                profiles.Add(PiLaunch.Profile(
                    binding.Profile,
                    program,
                    evidence,
                    PiLaunch.DeniedCommandsOf(policy.DeniedTools),
                    purpose,
                    binding.Execution.Model,
                    binding.Execution.Reasoning,
                    readOnly: purpose != AgentSessionPurpose.Implementation));
            }
            else
            {
                profiles.Add(ClaudeCodeLaunch.Profile(binding.Profile, policy, AgentExecutable, evidence, purpose, binding.Execution.Model, binding.Execution.Reasoning));
            }
        }

        var session = HerdrIsolatedCore.NewSessionName();

        var runtime = new HerdrAgentRuntime(new HerdrAgentRuntimeOptions
        {
            Session = session,
            Profiles = profiles,
            StartupTimeout = TimeSpan.FromSeconds(120),
        });

        return new AgentRuntimeHost(runtime, runtime, runtime, runtime, runtime, session);
    }

    public Task<bool> StopOrphanedSessionAsync(string session, CancellationToken cancellationToken)
        => HerdrIsolatedCore.IsOwnSessionName(session)
            ? HerdrIsolatedCore.StopAsync(session, locator.Find(HerdrExecutable) ?? HerdrExecutable, TimeSpan.FromSeconds(30), cancellationToken)
            : Task.FromResult(false);
}

/// <summary>Finds a program on PATH the way the shell would for the extensions Windows toolchains use. Read-only.</summary>
internal sealed class ExecutableLocator(Func<string, string?> getEnvironmentVariable)
{
    private static readonly string[] WindowsExtensions = [".exe", ".cmd", ".bat", ".com"];

    public string? Find(string name)
    {
        var extensions = OperatingSystem.IsWindows() ? WindowsExtensions : [string.Empty];

        foreach (var directory in (getEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim('"'), name + extension);

                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // a malformed PATH entry cannot hold the program
                }
            }
        }

        return null;
    }
}

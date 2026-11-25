using System.Text.Json;
using AgenticWorkflow.Application.Evidence;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Revisions;

namespace AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;

/// <summary>Where and how a Claude Code session's evidence hooks are installed.</summary>
public sealed record ClaudeEvidenceOptions
{
    /// <summary>The AgenticWorkflow-owned hook executable Claude Code starts for each observed event.</summary>
    public required string HookExecutable { get; init; }

    /// <summary>Directory under which per-session channels are created. Outside any workspace. Defaults to a folder in the user's temp directory.</summary>
    public string? EvidenceRoot { get; init; }

    /// <summary>
    /// Whether sessions are also given the workflow's clarification tool: an MCP server hosted by the same executable, a hook that records the agent's
    /// question and stops its turn, and an allow rule for exactly that tool. Off means the agent has no way to ask and is not told it may.
    /// </summary>
    public bool OfferClarification { get; init; }

    /// <summary>
    /// Whether sessions are also given the workflow's WHAT tool, through the same MCP server and hook executable: the agent can propose the WHAT of the task
    /// for a person to approve, and its turn is stopped once the proposal is recorded. Off means the agent has no way to propose and is not told it may.
    /// </summary>
    public bool OfferWhat { get; init; }

    /// <summary>
    /// Whether sessions are also given the workflow's HOW tool, through the same MCP server and hook executable: the agent can propose its implementation
    /// strategy for a person to approve, and its turn is stopped once the proposal is recorded. Off means the agent has no way to propose and is not told it may.
    /// </summary>
    public bool OfferHow { get; init; }

    /// <summary>
    /// Whether sessions are also given the workflow's reclassification tool, through the same MCP server and hook executable: the agent can report new evidence that
    /// invalidates a stage the workflow already settled, and its turn is stopped once the report is recorded so no further edit is made under the stale assumption.
    /// It can only add rigor. Off means the agent has no way to report and is not told it may.
    /// </summary>
    public bool OfferReclassification { get; init; }

    /// <summary>
    /// Whether sessions are also given the workflow's review-request tool: the agent can ask for an independent review of its verified work. It is additive, never
    /// stops the turn and cannot be withdrawn. Off means the agent has no way to ask and is not told it may.
    /// </summary>
    public bool OfferReviewRequest { get; init; }

    /// <summary>
    /// Whether sessions are given the review-report tool, and only that: the session is an independent reviewer, and its turn is stopped once its structured report
    /// is recorded. Set on the profile used for review sessions, never on an implementer's.
    /// </summary>
    public bool OfferReview { get; init; }

    /// <summary>
    /// Whether sessions are also given the workflow's Proof Contract tool: the implementer can submit the structured contract an independent prover will judge. It is additive,
    /// never stops the turn and cannot be withdrawn. Off means the agent has no way to submit one and is not told it may.
    /// </summary>
    public bool OfferProveRequest { get; init; }

    /// <summary>
    /// Whether sessions are given the proof-report tool, and only that: the session is an independent prover, and its turn is stopped once its structured proof is recorded.
    /// Set on the profile used for prove sessions, never on an implementer's.
    /// </summary>
    public bool OfferProve { get; init; }

    /// <summary>How the session's harness is told about the channel. Null means Claude Code, the original contract.</summary>
    public IEvidenceChannelHarness? Harness { get; init; }
}

/// <summary>
/// The controller-owned, ephemeral evidence channel of ONE Claude Code session. It owns a directory outside the workspace, the
/// per-session <c>--settings</c> file that points Claude Code's hooks at the AgenticWorkflow hook executable, and the session
/// id the controller chose (<c>--session-id</c>), which the hook uses to refuse events of any other session.
/// <para>
/// Nothing global or project-level is touched: the hooks exist only because the session was launched with the settings file
/// as an additional, session-only settings source; Claude Code merges hooks from every source, so existing user and project
/// hooks keep running. The hooks are observational and decide nothing. The directory is deleted when the channel is disposed
/// (the session ended).
/// </para>
/// </summary>
public sealed class ClaudeEvidenceChannel : IDisposable
{
    private readonly string _directory;
    private readonly string _runId;
    private readonly string _sessionId;
    private readonly string _settingsPath;
    private readonly string? _mcpConfigPath;
    private readonly IEvidenceChannelHarness _harness;
    private readonly ChannelLaunchFiles _files;
    private readonly bool _clarification;
    private readonly bool _what;
    private readonly bool _how;
    private readonly bool _reclassification;
    private readonly bool _reviewRequest;
    private readonly bool _review;
    private readonly bool _proveRequest;
    private readonly bool _prove;
    private bool _disposed;

    private ClaudeEvidenceChannel(string directory, string runId, string sessionId, IEvidenceChannelHarness harness, ChannelLaunchFiles files, bool clarification, bool what, bool how, bool reclassification, bool reviewRequest, bool review, bool proveRequest, bool prove)
    {
        _harness = harness;
        _files = files;
        var settingsPath = files.LaunchFilePath;
        var mcpConfigPath = files.McpConfigPath;
        _reviewRequest = reviewRequest;
        _review = review;
        _proveRequest = proveRequest;
        _prove = prove;
        _directory = directory;
        _runId = runId;
        _sessionId = sessionId;
        _settingsPath = settingsPath;
        _mcpConfigPath = mcpConfigPath;
        _clarification = clarification;
        _what = what;
        _how = how;
        _reclassification = reclassification;
    }

    /// <summary>The session-only MCP configuration that hosts the workflow tools (clarification, WHAT), or null when the session was given none.</summary>
    public string? McpConfigPath => _mcpConfigPath;

    /// <summary>True when this session was launched with the clarification tool, so its questions can be collected.</summary>
    public bool OffersClarification => _clarification;

    /// <summary>True when this session was launched with the WHAT tool, so its proposals can be collected.</summary>
    public bool OffersWhat => _what;

    /// <summary>True when this session was launched with the HOW tool, so its proposals can be collected.</summary>
    public bool OffersHow => _how;

    public bool OffersReclassification => _reclassification;

    /// <summary>True when this session was launched with the review-request tool, so its requests can be collected.</summary>
    public bool OffersReviewRequest => _reviewRequest;

    /// <summary>True when this session was launched with the review-report tool, so its report can be collected.</summary>
    public bool OffersReview => _review;

    /// <summary>True when this session was launched with the Proof Contract tool, so its contract can be collected.</summary>
    public bool OffersProveRequest => _proveRequest;

    /// <summary>True when this session was launched with the proof-report tool, so its proof can be collected.</summary>
    public bool OffersProve => _prove;

    /// <summary>The Claude session id the session must be launched with.</summary>
    public string SessionId => _sessionId;

    public string Directory => _directory;

    /// <summary>The file the session's harness is launched with: Claude Code's settings file, or Pi's extension.</summary>
    public string SettingsPath => _settingsPath;

    /// <summary>True when the harness's own tool executions are observed here, so a check it ran can stand in for a controller run.</summary>
    public bool ObservesExecutions => _harness.ObservesExecutions;

    /// <summary>A problem when the launched session did not start as it was asked to (the harness's own report disagrees with the request), null otherwise.</summary>
    public Task<string?> VerifyStartedAsync(CancellationToken cancellationToken) => _harness.VerifyStartedAsync(_directory, _sessionId, cancellationToken);

    /// <summary>
    /// Creates the channel, or returns null (with <paramref name="failure"/> set) when evidence cannot be set up; the session then
    /// simply runs without it. Nothing here may fail a task.
    /// </summary>
    public static ClaudeEvidenceChannel? TryCreate(
        ClaudeEvidenceOptions options,
        string workspacePath,
        IReadOnlyList<VerificationCheckSpec> checks,
        out string? failure)
        => TryCreate(options, workspacePath, checks, sessionId: null, out failure);

    /// <summary>
    /// As below, optionally also giving the session the clarification tool (<paramref name="clarification"/>). A channel with the tool needs no checks to
    /// observe: the tool is useful to a session that proves nothing.
    /// </summary>
    public static ClaudeEvidenceChannel? TryCreate(
        ClaudeEvidenceOptions options,
        string workspacePath,
        IReadOnlyList<VerificationCheckSpec> checks,
        string? sessionId,
        bool clarification,
        out string? failure)
        => Create(options, workspacePath, checks, sessionId, clarification, what: false, how: false, reclassification: false, reviewRequest: false, review: false, proveRequest: false, prove: false, out failure);

    /// <summary>As above, also giving the session the WHAT tool (<paramref name="what"/>). A channel with either tool needs no checks to observe.</summary>
    public static ClaudeEvidenceChannel? TryCreate(
        ClaudeEvidenceOptions options,
        string workspacePath,
        IReadOnlyList<VerificationCheckSpec> checks,
        string? sessionId,
        bool clarification,
        bool what,
        out string? failure)
        => Create(options, workspacePath, checks, sessionId, clarification, what, how: false, reclassification: false, reviewRequest: false, review: false, proveRequest: false, prove: false, out failure);

    /// <summary>As above, also giving the session the HOW tool (<paramref name="how"/>). A channel with any tool needs no checks to observe.</summary>
    public static ClaudeEvidenceChannel? TryCreate(
        ClaudeEvidenceOptions options,
        string workspacePath,
        IReadOnlyList<VerificationCheckSpec> checks,
        string? sessionId,
        bool clarification,
        bool what,
        bool how,
        out string? failure)
        => Create(options, workspacePath, checks, sessionId, clarification, what, how, reclassification: false, reviewRequest: false, review: false, proveRequest: false, prove: false, out failure);

    /// <summary>As above, also giving the session the reclassification tool (<paramref name="reclassification"/>). A channel with any tool needs no checks to observe.</summary>
    public static ClaudeEvidenceChannel? TryCreate(
        ClaudeEvidenceOptions options,
        string workspacePath,
        IReadOnlyList<VerificationCheckSpec> checks,
        string? sessionId,
        bool clarification,
        bool what,
        bool how,
        bool reclassification,
        out string? failure)
        => Create(options, workspacePath, checks, sessionId, clarification, what, how, reclassification, reviewRequest: false, review: false, proveRequest: false, prove: false, out failure);

    /// <summary>As above, also giving the session the review-request tool (<paramref name="reviewRequest"/>) or, for an independent reviewer, the review-report tool (<paramref name="review"/>).</summary>
    public static ClaudeEvidenceChannel? TryCreate(
        ClaudeEvidenceOptions options,
        string workspacePath,
        IReadOnlyList<VerificationCheckSpec> checks,
        string? sessionId,
        bool clarification,
        bool what,
        bool how,
        bool reclassification,
        bool reviewRequest,
        bool review,
        out string? failure)
        => Create(options, workspacePath, checks, sessionId, clarification, what, how, reclassification, reviewRequest, review, proveRequest: false, prove: false, out failure);

    /// <summary>As above, also giving the session the Proof Contract tool (<paramref name="proveRequest"/>) or, for an independent prover, the proof-report tool (<paramref name="prove"/>).</summary>
    public static ClaudeEvidenceChannel? TryCreate(
        ClaudeEvidenceOptions options,
        string workspacePath,
        IReadOnlyList<VerificationCheckSpec> checks,
        string? sessionId,
        bool clarification,
        bool what,
        bool how,
        bool reclassification,
        bool reviewRequest,
        bool review,
        bool proveRequest,
        bool prove,
        out string? failure)
        => Create(options, workspacePath, checks, sessionId, clarification, what, how, reclassification, reviewRequest, review, proveRequest, prove, out failure);

    /// <summary>
    /// As above, for a conversation that already has an id (a resumed one): the hooks are bound to exactly that Claude session id, so evidence of any
    /// other conversation is still ignored. A null <paramref name="sessionId"/> means a new id is generated, which the launch must then be given.
    /// </summary>
    public static ClaudeEvidenceChannel? TryCreate(
        ClaudeEvidenceOptions options,
        string workspacePath,
        IReadOnlyList<VerificationCheckSpec> checks,
        string? sessionId,
        out string? failure)
        => Create(options, workspacePath, checks, sessionId, clarification: false, what: false, how: false, reclassification: false, reviewRequest: false, review: false, proveRequest: false, prove: false, out failure);

    private static ClaudeEvidenceChannel? Create(
        ClaudeEvidenceOptions options,
        string workspacePath,
        IReadOnlyList<VerificationCheckSpec> checks,
        string? sessionId,
        bool clarification,
        bool what,
        bool how,
        bool reclassification,
        bool reviewRequest,
        bool review,
        bool proveRequest,
        bool prove,
        out string? failure)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(checks);

        failure = null;
        string? directory = null;

        try
        {
            if (!File.Exists(options.HookExecutable))
            {
                failure = $"the evidence hook executable '{options.HookExecutable}' does not exist";
                return null;
            }

            if (checks.Count == 0 && !clarification && !what && !how && !reclassification && !reviewRequest && !review && !proveRequest && !prove)
            {
                failure = "there are no checks to observe";
                return null;
            }

            var root = options.EvidenceRoot ?? Path.Combine(Path.GetTempPath(), "AgenticWorkflow", "evidence");
            var runId = Guid.NewGuid().ToString("N");
            sessionId ??= Guid.NewGuid().ToString();

            directory = Path.GetFullPath(Path.Combine(root, runId));

            if (IsInside(directory, workspacePath))
            {
                failure = "the evidence directory would be inside the workspace";
                return null;
            }

            System.IO.Directory.CreateDirectory(directory);

            var config = new ChannelConfig
            {
                RunId = runId,
                SessionId = sessionId,
                Workspace = Path.GetFullPath(workspacePath),
                Clarification = clarification,
                What = what,
                How = how,
                Reclassification = reclassification,
                ReviewRequest = reviewRequest,
                Review = review,
                ProveRequest = proveRequest,
                Prove = prove,
                Candidates = checks
                    .Select(c => new ChannelCandidate { Executable = c.Executable, Arguments = c.Arguments, WorkingDirectory = c.WorkingDirectory })
                    .ToArray(),
            };

            File.WriteAllText(Path.Combine(directory, ClaudeEvidenceFormat.ConfigFile), JsonSerializer.Serialize(config, ClaudeEvidenceFormat.Json));

            var harness = options.Harness ?? ClaudeChannelHarness.Instance;
            var files = harness.Prepare(options.HookExecutable, directory, new ChannelOffers(clarification, what, how, reclassification, reviewRequest, review, proveRequest, prove));

            return new ClaudeEvidenceChannel(directory, runId, sessionId, harness, files, clarification, what, how, reclassification, reviewRequest, review, proveRequest, prove);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            failure = $"{e.GetType().Name}: {e.Message}";

            TryDelete(directory);
            return null;
        }
    }

    /// <summary>The launch arguments with the session-only settings and session id added right after the executable. The original list is unchanged.</summary>
    public IReadOnlyList<string> Augment(IReadOnlyList<string> argv, bool nameSession = true)
    {
        ArgumentNullException.ThrowIfNull(argv);

        // nameSession=false: the caller's argv already names (or resumes) this channel's conversation, so it is not named twice.
        return _harness.Augment(argv, _files, _sessionId, nameSession);
    }

    /// <summary>
    /// The questions the agent of THIS session raised with the clarification tool, written between <paramref name="fromTicks"/> (inclusive) and
    /// <paramref name="beforeTicks"/> (exclusive; null means "to now"), oldest first. Only records the hook wrote for this channel's own session count: a record
    /// of another session, an unreadable one or a problem record is never a question.
    /// </summary>
    public IReadOnlyList<AgentClarificationSignal> CollectClarifications(long fromTicks, long? beforeTicks)
        => CollectSignals(
            fromTicks,
            beforeTicks,
            record => record is { Kind: ClaudeEvidenceFormat.KindClarification, Question: { } question, Reason: { } reason }
                ? new AgentClarificationSignal(question, reason)
                : null);

    /// <summary>
    /// The WHAT proposals the agent of THIS session made with the WHAT tool, between <paramref name="fromTicks"/> (inclusive) and <paramref name="beforeTicks"/>
    /// (exclusive; null means "to now"), oldest first, under the same identity rules as <see cref="CollectClarifications"/>.
    /// </summary>
    public IReadOnlyList<AgentWhatSignal> CollectWhatProposals(long fromTicks, long? beforeTicks)
        => CollectSignals(
            fromTicks,
            beforeTicks,
            record => record is { Kind: ClaudeEvidenceFormat.KindWhat, What: { } what }
                ? new AgentWhatSignal(what.Objective, what.RequiredBehavior, what.Scope, what.Constraints, what.NonGoals, what.AcceptanceConditions, what.Rationale)
                : null);

    /// <summary>
    /// The HOW proposals the agent of THIS session made with the HOW tool, between <paramref name="fromTicks"/> (inclusive) and <paramref name="beforeTicks"/>
    /// (exclusive; null means "to now"), oldest first, under the same identity rules as <see cref="CollectClarifications"/>.
    /// </summary>
    public IReadOnlyList<AgentHowSignal> CollectHowProposals(long fromTicks, long? beforeTicks)
        => CollectSignals(
            fromTicks,
            beforeTicks,
            record => record is { Kind: ClaudeEvidenceFormat.KindHow, How: { } how }
                ? new AgentHowSignal(how.Strategy, how.Components, how.DataAndControlFlow, how.Compatibility, how.RisksAndTradeoffs, how.VerificationApproach, how.Rationale)
                : null);

    /// <summary>
    /// The reclassification reports the agent of THIS session made with the reclassification tool, between <paramref name="fromTicks"/> (inclusive) and
    /// <paramref name="beforeTicks"/> (exclusive; null means "to now"), oldest first, under the same identity rules as <see cref="CollectClarifications"/>.
    /// </summary>
    public IReadOnlyList<AgentReclassificationSignal> CollectReclassifications(long fromTicks, long? beforeTicks)
        => CollectSignals(
            fromTicks,
            beforeTicks,
            record => record is { Kind: ClaudeEvidenceFormat.KindReclassification, Reclassification: { } report }
                ? new AgentReclassificationSignal(report.Evidence, report.Stage == "what" ? ReclassificationStage.What : ReclassificationStage.How, report.Reason)
                : null);

    /// <summary>
    /// The review requests the agent of THIS session made with the review-request tool, between <paramref name="fromTicks"/> (inclusive) and
    /// <paramref name="beforeTicks"/> (exclusive; null means "to now"), oldest first, under the same identity rules as <see cref="CollectClarifications"/>.
    /// </summary>
    public IReadOnlyList<AgentReviewRequestSignal> CollectReviewRequests(long fromTicks, long? beforeTicks)
        => CollectSignals(
            fromTicks,
            beforeTicks,
            record => record is { Kind: ClaudeEvidenceFormat.KindReviewRequest, Reason: { } reason }
                ? new AgentReviewRequestSignal(reason)
                : null);

    /// <summary>
    /// The review reports the reviewer of THIS session submitted with the review tool, between <paramref name="fromTicks"/> (inclusive) and
    /// <paramref name="beforeTicks"/> (exclusive; null means "to now"), oldest first, under the same identity rules as <see cref="CollectClarifications"/>. Only records the
    /// hook wrote for this channel's own session count; nothing is read from the terminal or the transcript.
    /// </summary>
    public IReadOnlyList<AgentReviewSubmission> CollectReviewSubmissions(long fromTicks, long? beforeTicks)
        => CollectSignals(
            fromTicks,
            beforeTicks,
            record => record is { Kind: ClaudeEvidenceFormat.KindReview, Review: { } review }
                ? new AgentReviewSubmission(
                    review.Verdict,
                    review.Summary,
                    [.. review.Findings.Select(f => new AgentReviewFinding(f.Title, f.Evidence, f.Impact, f.Recommendation, f.Location))],
                    review.Notes)
                : null);

    /// <summary>
    /// The Proof Contracts the implementer of THIS session submitted with the contract tool, between <paramref name="fromTicks"/> (inclusive) and
    /// <paramref name="beforeTicks"/> (exclusive; null means "to now"), oldest first, under the same identity rules as <see cref="CollectClarifications"/>.
    /// </summary>
    public IReadOnlyList<AgentProveRequestSignal> CollectProveRequests(long fromTicks, long? beforeTicks)
        => CollectSignals(
            fromTicks,
            beforeTicks,
            record => record is { Kind: ClaudeEvidenceFormat.KindProveRequest, ProofContract: { } contract }
                ? new AgentProveRequestSignal(
                    contract.Rationale,
                    [.. contract.Claims.Select(c => new AgentProofClaim(
                        c.Id,
                        c.Statement,
                        [.. c.Evidence.Select(e => new AgentProofEvidenceRequirement(e.Kind, e.Subject))],
                        c.AcceptanceCriterion))])
                : null);

    /// <summary>
    /// The proofs the prover of THIS session submitted with the proof tool, between <paramref name="fromTicks"/> (inclusive) and <paramref name="beforeTicks"/>
    /// (exclusive; null means "to now"), oldest first, under the same identity rules as <see cref="CollectClarifications"/>. Only records the hook wrote for this
    /// channel's own session count; nothing is read from the terminal or the transcript.
    /// </summary>
    public IReadOnlyList<AgentProofSubmission> CollectProofSubmissions(long fromTicks, long? beforeTicks)
        => CollectSignals(
            fromTicks,
            beforeTicks,
            record => record is { Kind: ClaudeEvidenceFormat.KindProof, Proof: { } proof }
                ? new AgentProofSubmission(
                    proof.Summary,
                    [.. proof.Claims.Select(c => new AgentProofClaimResult(c.ClaimId, c.Verdict, [.. c.Evidence], c.Summary))])
                : null);

    private IReadOnlyList<TSignal> CollectSignals<TSignal>(long fromTicks, long? beforeTicks, Func<RawEvent, TSignal?> pick)
        where TSignal : class
    {
        ThrowIfDisposed();

        var found = new List<(long Ticks, string Name, TSignal Signal)>();
        var observed = new HashSet<(string Kind, string ToolUseId)>();

        foreach (var name in System.IO.Directory.EnumerateFiles(_directory, ClaudeEvidenceFormat.EventPrefix + "*").Select(Path.GetFileName).OfType<string>())
        {
            if (ClaudeEvidenceFormat.TicksOf(name, ClaudeEvidenceFormat.EventPrefix) is not { } ticks || ticks < fromTicks || (beforeTicks is { } limit && ticks >= limit))
                continue;

            try
            {
                var record = JsonSerializer.Deserialize<RawEvent>(File.ReadAllText(Path.Combine(_directory, name)), ClaudeEvidenceFormat.Json);

                if (record is not null
                    && string.Equals(record.SessionId, _sessionId, StringComparison.Ordinal)
                    && string.Equals(record.RunId, _runId, StringComparison.Ordinal)
                    && pick(record) is { } signal)
                {
                    // One tool call is one signal, however many times it was observed: a second record of the same call (this session's same tool-use id) never becomes a second signal.
                    if (record.ToolUseId is { Length: > 0 } callId && !observed.Add((record.Kind, callId)))
                        continue;

                    found.Add((ticks, name, signal));
                }
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                // An unreadable record is not a signal; it cannot make one up.
            }
        }

        return [.. found.OrderBy(f => f.Ticks).ThenBy(f => f.Name, StringComparer.Ordinal).Select(f => f.Signal)];
    }

    /// <summary>Starts a new turn window: records written from now on belong to the next turn.</summary>
    public void MarkTurnStart()
    {
        ThrowIfDisposed();

        var ticks = DateTime.UtcNow.Ticks;
        File.WriteAllText(Path.Combine(_directory, ClaudeEvidenceFormat.MarkFileName(ticks)), string.Empty);
    }

    /// <summary>
    /// Records that the controller sends this session a turn at <paramref name="ticks"/>. A reviewer or prover accepts one structured report per turn: the controller
    /// may refuse a report and ask for one correction in the same conversation, and only a later turn lets the session's report tool accept another. A session that submits
    /// nothing structured has no use for the record, so none is written.
    /// </summary>
    public void MarkSubmissionTurn(long ticks)
    {
        ThrowIfDisposed();

        if (!_review && !_prove)
            return;

        File.WriteAllText(Path.Combine(_directory, ClaudeEvidenceFormat.TurnFileName(ticks)), string.Empty);
    }

    /// <summary>What the hooks recorded since the latest <see cref="MarkTurnStart"/>, translated. Never throws for bad records; they become problems.</summary>
    public ObservedTurnEvidence Collect()
    {
        ThrowIfDisposed();

        var problems = new List<string>();
        var names = System.IO.Directory.EnumerateFiles(_directory).Select(Path.GetFileName).OfType<string>().ToList();

        var marks = names.Select(n => ClaudeEvidenceFormat.TicksOf(n, ClaudeEvidenceFormat.MarkPrefix)).OfType<long>().ToList();

        if (marks.Count == 0)
        {
            return new ObservedTurnEvidence(ClaudeEvidenceFormat.RuntimeName, [], ["no_turn_window: no turn was started on this channel"]);
        }

        var windowStart = marks.Max();
        var events = new List<RawEvent>();

        foreach (var name in names.Where(n => n.StartsWith(ClaudeEvidenceFormat.EventPrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            if (ClaudeEvidenceFormat.TicksOf(name, ClaudeEvidenceFormat.EventPrefix) is not { } ticks)
            {
                problems.Add($"unrecognised_record: '{name}'");
                continue;
            }

            if (ticks <= windowStart)
            {
                continue; // an earlier turn: never evidence for this one
            }

            try
            {
                var evt = JsonSerializer.Deserialize<RawEvent>(File.ReadAllText(Path.Combine(_directory, name)), ClaudeEvidenceFormat.Json);
                events.Add(evt ?? throw new JsonException("null record"));
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                problems.Add($"unreadable_record: '{name}' ({e.GetType().Name})");
            }
        }

        // A reviewer or prover is sent one turn per report attempt (a correction or a reminder is a second turn in the same conversation), each with its own prompt.
        var controllerTurns = names.Count(n => ClaudeEvidenceFormat.TicksOf(n, ClaudeEvidenceFormat.TurnPrefix) is { } sent && sent >= windowStart);

        return ClaudeEvidenceTranslator.Translate(_runId, _sessionId, events, problems, controllerTurns);
    }

    /// <summary>The handler time (ms) of every record written so far, for overhead measurement. Diagnostic only.</summary>
    public IReadOnlyList<long> HandlerMilliseconds()
    {
        var result = new List<long>();

        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, ClaudeEvidenceFormat.EventPrefix + "*"))
        {
            try
            {
                if (JsonSerializer.Deserialize<RawEvent>(File.ReadAllText(path), ClaudeEvidenceFormat.Json) is { } evt)
                {
                    result.Add(evt.HandlerMs);
                }
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                // diagnostics only
            }
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TryDelete(_directory);
    }

    /// <summary>The MCP configuration of a session that may ask a question: this executable, started in its MCP-server mode. Session-only; no user or project file is read or written.</summary>
    internal static string McpConfigJson(string hookExecutable, string channelDirectory)
        => JsonSerializer.Serialize(
            new
            {
                mcpServers = new Dictionary<string, object>
                {
                    [ClaudeEvidenceFormat.ServerName] = new { command = Path.GetFullPath(hookExecutable), args = new[] { ClaudeEvidenceFormat.McpModeArgument, channelDirectory } },
                },
            },
            new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// The settings JSON for <c>--settings</c>: observational command hooks and, for a session that may ask a question, the hook that records it and the
    /// allow rule for exactly that tool. Nothing else.
    /// </summary>
    internal static string SettingsJson(string hookExecutable, string channelDirectory, bool clarification = false, bool what = false, bool how = false, bool reclassification = false, bool reviewRequest = false, bool review = false, bool proveRequest = false, bool prove = false)
    {
        var command = $"\"{Forward(hookExecutable)}\" \"{Forward(channelDirectory)}\"";

        object Hook() => new { type = "command", command };

        var postToolUse = new List<object> { new { matcher = "Bash|PowerShell", hooks = new[] { Hook() } } };

        if (clarification)
            postToolUse.Add(new { matcher = ClaudeEvidenceFormat.ClarificationToolFullName, hooks = new[] { Hook() } });

        if (what)
            postToolUse.Add(new { matcher = ClaudeEvidenceFormat.WhatToolFullName, hooks = new[] { Hook() } });

        if (how)
            postToolUse.Add(new { matcher = ClaudeEvidenceFormat.HowToolFullName, hooks = new[] { Hook() } });

        if (reclassification)
            postToolUse.Add(new { matcher = ClaudeEvidenceFormat.ReclassificationToolFullName, hooks = new[] { Hook() } });

        if (reviewRequest)
            postToolUse.Add(new { matcher = ClaudeEvidenceFormat.ReviewRequestToolFullName, hooks = new[] { Hook() } });

        if (review)
            postToolUse.Add(new { matcher = ClaudeEvidenceFormat.ReviewToolFullName, hooks = new[] { Hook() } });

        if (proveRequest)
            postToolUse.Add(new { matcher = ClaudeEvidenceFormat.ProveRequestToolFullName, hooks = new[] { Hook() } });

        if (prove)
            postToolUse.Add(new { matcher = ClaudeEvidenceFormat.ProofToolFullName, hooks = new[] { Hook() } });

        var settings = new Dictionary<string, object>
        {
            ["hooks"] = new Dictionary<string, object[]>
            {
                // Outcome and duration of command tools only; the batch event carries every tool, so reads cost nothing extra.
                ["PostToolUse"] = [.. postToolUse],
                ["PostToolUseFailure"] = [new { matcher = "Bash|PowerShell", hooks = new[] { Hook() } }],
                ["PostToolBatch"] = [new { hooks = new[] { Hook() } }],
            },
        };

        if (clarification || what || how || reclassification || reviewRequest || review || proveRequest || prove)
        {
            var allowed = new List<string>();

            if (clarification)
                allowed.Add(ClaudeEvidenceFormat.ClarificationToolFullName);

            if (what)
                allowed.Add(ClaudeEvidenceFormat.WhatToolFullName);

            if (how)
                allowed.Add(ClaudeEvidenceFormat.HowToolFullName);

            if (reclassification)
                allowed.Add(ClaudeEvidenceFormat.ReclassificationToolFullName);

            if (reviewRequest)
                allowed.Add(ClaudeEvidenceFormat.ReviewRequestToolFullName);

            if (review)
                allowed.Add(ClaudeEvidenceFormat.ReviewToolFullName);

            if (proveRequest)
                allowed.Add(ClaudeEvidenceFormat.ProveRequestToolFullName);

            if (prove)
                allowed.Add(ClaudeEvidenceFormat.ProofToolFullName);

            settings["permissions"] = new { allow = allowed.ToArray() };
        }

        return JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Forward(string path)
    {
        if (path.Contains('"'))
        {
            throw new ArgumentException("A path used in a hook command must not contain a double quote.", nameof(path));
        }

        return path.Replace('\\', '/');
    }

    private static bool IsInside(string path, string container)
    {
        var parent = Path.GetFullPath(container).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

        return Path.GetFullPath(path).StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string? directory)
    {
        if (directory is null)
        {
            return;
        }

        try
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort: the directory is ephemeral and outside the workspace
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}

using System.Text.Json;
using AgenticWorkflow.Application.Evidence;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;

namespace AgenticWorkflow.Runtime.Herdr;

/// <summary>Settings of one <see cref="HerdrAgentRuntime"/>.</summary>
public sealed record HerdrAgentRuntimeOptions
{
    /// <summary>
    /// The <c>HC_SESSION</c> of the isolated core this runtime owns (for example <c>agenticworkflow-m2b-a1b2c3</c>).
    /// Required: the runtime never talks to the user's default core.
    /// </summary>
    public required string Session { get; init; }

    /// <summary>The launch configuration of every workflow profile this runtime may start. Anything else is refused.</summary>
    public required IReadOnlyList<AgentLaunchProfile> Profiles { get; init; }

    public string Executable { get; init; } = "hc";

    /// <summary>Time for a freshly launched agent to report its first structured status (<see cref="ReadyStatuses"/>).</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long a person attending is waited for while the agent is starting and needs them (a blocked status, or no ready status within <see cref="StartupTimeout"/>, which
    /// is how a first-run question the harness does not report looks). Only used when the run asked to be told about such a wait; otherwise a startup that needs a person fails.
    /// </summary>
    public TimeSpan StartupInteractionTimeout { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a sent prompt may leave the agent idle, with no turn started, before it is reported as not taken (a prompt that was pasted but never submitted would otherwise be
    /// waited on for as long as the turn timeout). The session is left as it is; nothing is typed again.
    /// </summary>
    public TimeSpan SubmissionTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Status words that mean "ready for a first prompt".</summary>
    public IReadOnlyList<string> ReadyStatuses { get; init; } = ["idle"];

    /// <summary>Stop the isolated core when the runtime is disposed. Only ever applies to <see cref="Session"/>.</summary>
    public bool StopCoreOnDispose { get; init; } = true;

    /// <summary>Environment variable prefixes removed before the core (and so its terminals) is started.</summary>
    public IReadOnlyList<string> ScrubbedEnvironmentPrefixes { get; init; } = ["CLAUDE"];
}

/// <summary>Starts, stops and checks the isolated core. Narrow on purpose: these are the only things the CLI is used for.</summary>
internal interface IHerdrServerControl
{
    Task EnsureStartedAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

internal sealed class HcServerControl(HcClient client) : IHerdrServerControl
{
    public async Task EnsureStartedAsync(CancellationToken cancellationToken) => await client.StartServerAsync(cancellationToken).ConfigureAwait(false);

    public async Task StopAsync(CancellationToken cancellationToken) => await client.StopServerAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// <see cref="IAgentRuntime"/> over Herdr. One <see cref="StartSessionAsync"/> launches one long-lived agent terminal in
/// an isolated core; every <see cref="SendTurnAsync"/> to it is another prompt to the same process.
/// <list type="bullet">
/// <item>Transport: JSON-RPC over <c>hc connect</c>. The CLI is used only to start and stop the core.</item>
/// <item>Lifecycle comes from the program's own structured status (<c>idle/working/done/blocked/error</c>) and the
/// terminal's <c>turn</c> counter, delivered by a dedicated <c>events.subscribe</c> connection and confirmed against
/// <c>terminal.get</c>. Screen text is never read to decide anything.</item>
/// <item>Never <c>force</c>s input: a blocked agent is reported, not answered. The agent's own permission prompt, question or login is a native
/// human interaction: Herdr shows it, the person answers it there, and this runtime only observes that the agent is blocked and later working again
/// (<see cref="AgentTurnStatus.AwaitingHuman"/>, <see cref="AgentTurnStatus.Resumed"/>). It owns no part of the decision.</item>
/// <item>Touches only terminals it created (labelled and named with this runtime's marker).</item>
/// <item>Optional conversation capability (<see cref="IAgentConversationRuntime"/>): Herdr itself has no resume operation and no notion of the agent conversation,
/// so a profile that has an <see cref="IAgentConversationLaunch"/> launches every session with a conversation reference chosen up front and resumes it by
/// launching a new terminal through the agent program's own public resume flag, with the profile's full launch configuration (hard limits included).
/// A process is not a conversation: stopping the terminal ends the process, not the conversation.</item>
/// <item>Optional evidence capability (<see cref="IExecutionEvidenceSource"/>): a profile configured with
/// <see cref="ClaudeEvidenceOptions"/> launches its session with a session-only, observational hook channel; any failure to
/// set that up just means the session has no evidence.</item>
/// <item>Optional signal capability (<see cref="IAgentSignalSource"/>): the same channel also gives the session the workflow's clarification tool (see
/// <see cref="ClaudeClarificationServer"/>), and the questions the agent raised in a turn are read back from the records of that session's own channel,
/// windowed by the moment each turn was sent. The runtime never reads screen text or the transcript to find one.</item>
/// </list>
/// </summary>
public sealed class HerdrAgentRuntime : IAgentRuntime, IExecutionEvidenceSource, IAgentConversationRuntime, IAgentSignalSource, IAsyncDisposable
{
    private static readonly TimeSpan CheckIn = TimeSpan.FromSeconds(5);

    private readonly HerdrAgentRuntimeOptions _options;
    private readonly IHerdrServerControl _server;
    private readonly IHerdrTransportFactory _transports;
    private readonly Dictionary<(ProfileId Profile, AgentSessionPurpose Purpose), AgentLaunchProfile> _profiles;
    private readonly Dictionary<AgentSessionId, SessionState> _sessions = [];
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly HerdrEventHub _hub = new();
    private readonly string _marker = Guid.NewGuid().ToString("N")[..8];

    private HerdrRpcConnection? _commands;
    private HerdrRpcConnection? _events;
    private int _sessionCounter;
    private bool _coreStartedByThisRuntime;
    private bool _disposed;

    public HerdrAgentRuntime(HerdrAgentRuntimeOptions options)
        : this(options, null, null)
    {
    }

    internal HerdrAgentRuntime(HerdrAgentRuntimeOptions options, IHerdrServerControl? server, IHerdrTransportFactory? transports)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Session))
            throw new ArgumentException("An isolated HC_SESSION is required; the default core is never used.", nameof(options));

        if (options.Profiles.Count == 0)
            throw new ArgumentException("At least one launch profile must be configured.", nameof(options));

        _options = options;
        _profiles = options.Profiles.ToDictionary(p => (p.Profile, p.Purpose));

        var clientOptions = new HcClientOptions
        {
            Executable = options.Executable,
            Session = options.Session,
            ScrubbedEnvironmentPrefixes = options.ScrubbedEnvironmentPrefixes,
            CommandTimeout = options.CommandTimeout,
        };

        _server = server ?? new HcServerControl(new HcClient(clientOptions));
        _transports = transports ?? HcConnectTransport.Factory(clientOptions);
    }

    /// <summary>The label every terminal of this runtime carries (<c>aw_runtime=&lt;value&gt;</c>).</summary>
    public string Marker => _marker;

    // ---- IAgentRuntime ---------------------------------------------------------------------------------------------

    public Task<AgentSessionId> StartSessionAsync(AgentSessionRequest request, CancellationToken cancellationToken = default)
        => LaunchAsync(request, resume: null, cancellationToken);

    // ---- IAgentConversationRuntime ---------------------------------------------------------------------------------

    public bool CanResume(ProfileId profile)
        => _profiles.TryGetValue((profile, AgentSessionPurpose.Implementation), out var launch) && launch.Conversation is not null;

    public AgentConversationRef? ConversationOf(AgentSessionId session)
    {
        lock (_sessions)
            return _sessions.TryGetValue(session, out var state) ? state.Conversation : null;
    }

    public Task<AgentSessionId> ResumeSessionAsync(AgentSessionRequest request, AgentConversationRef conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(conversation);

        return LaunchAsync(request, conversation, cancellationToken);
    }

    /// <summary>
    /// Launches one terminal for <paramref name="request"/>: a new conversation, or (with <paramref name="resume"/>) a new process attached to the existing one.
    /// Both take the profile's fixed argv, so the hard limits are applied on every launch and never inherited from the conversation.
    /// </summary>
    private async Task<AgentSessionId> LaunchAsync(AgentSessionRequest request, AgentConversationRef? resume, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        // No fallback and no "closest" profile: an unconfigured profile is refused before anything is launched.
        if (!_profiles.TryGetValue((request.Profile, request.Purpose), out var launch))
            throw new AgentRuntimeException($"profile '{request.Profile}' has no launch configuration for {PurposeText(request.Purpose)} in this runtime");

        if (resume is not null)
            ValidateResumable(launch, request, resume);

        if (!Directory.Exists(request.WorkspacePath))
            throw new AgentRuntimeException($"workspace '{request.WorkspacePath}' does not exist");

        var conversationLaunch = launch.Conversation;
        var reference = resume?.Reference ?? conversationLaunch?.NewReference();

        return await Translate(async () =>
        {
            var commands = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            var number = Interlocked.Increment(ref _sessionCounter);
            var name = $"aw-{_marker}-{number}";

            // Optional and fail-safe: if the channel cannot be created the session runs without evidence.
            var channel = OpenEvidenceChannel(launch, request, reference, out var channelFailure);

            // A harness whose launch can only be verified through its channel (Pi) is never started without one.
            if (channel is null && launch.Evidence?.Harness is { RequiresChannel: true })
                throw new AgentRuntimeException($"the session's workflow channel could not be set up ({channelFailure}); nothing was started");

            IReadOnlyList<string> argv;

            if (conversationLaunch is null || reference is null)
            {
                argv = channel is null ? launch.Argv : channel.Augment(launch.Argv);
            }
            else
            {
                var named = resume is null ? conversationLaunch.ForNewConversation(launch.Argv, reference) : conversationLaunch.ForResume(launch.Argv, reference);
                argv = channel is null ? named : channel.Augment(named, nameSession: false);
            }

            JsonElement created;

            try
            {
                created = await CallAsync(
                    commands,
                    "terminal.run",
                    new
                    {
                        argv,
                        cwd = request.WorkspacePath,
                        name,
                        no_parent = true,
                        labels = new Dictionary<string, string>
                        {
                            ["aw_runtime"] = _marker,
                            ["aw_session"] = name,
                            ["aw_label"] = Sanitize(request.Label),
                        },
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                channel?.Dispose();
                throw;
            }

            var terminal = HerdrTerminal.FromJson(created);
            var session = new SessionState(new AgentSessionId(name), terminal.Address, _hub.Watch(terminal.Address))
            {
                Evidence = channel,
                Conversation = conversationLaunch is null || reference is null ? null : new AgentConversationRef(request.Profile, conversationLaunch.Runtime, reference),
            };

            lock (_sessions)
                _sessions[session.Id] = session;

            try
            {
                await WaitUntilReadyAsync(commands, session, resume is null ? null : conversationLaunch, request.StartupInteractions, cancellationToken).ConfigureAwait(false);

                // What the harness says it started with must be what was asked for: a harness that substitutes a model or clamps a level on its own is refused, never used.
                if (channel is not null && await channel.VerifyStartedAsync(cancellationToken).ConfigureAwait(false) is { } mismatch)
                    throw new AgentRuntimeException($"the agent did not start as requested: {mismatch}");
            }
            catch
            {
                await StopQuietlyAsync(session).ConfigureAwait(false);
                throw;
            }

            return session.Id;
        }).ConfigureAwait(false);
    }

    /// <summary>A reference is resumed only by the runtime kind and the profile it was issued for, and only if it is exactly the shape this runtime issues.</summary>
    private static void ValidateResumable(AgentLaunchProfile launch, AgentSessionRequest request, AgentConversationRef conversation)
    {
        if (launch.Conversation is not { } support)
            throw new AgentRuntimeException($"profile '{request.Profile}' cannot resume conversations in this runtime");

        if (conversation.Profile != request.Profile)
            throw new InvalidAgentConversationException($"the conversation was started under profile '{conversation.Profile}', not '{request.Profile}'");

        if (!string.Equals(conversation.Runtime, support.Runtime, StringComparison.Ordinal))
            throw new InvalidAgentConversationException($"the conversation belongs to runtime '{conversation.Runtime}', not '{support.Runtime}'");

        if (!support.IsValidReference(conversation.Reference))
            throw new InvalidAgentConversationException("the conversation reference is malformed");
    }

    public async Task<AgentTurnNumber> SendTurnAsync(AgentSessionId session, string prompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("A turn needs a prompt.", nameof(prompt));

        var state = Find(session);

        return await Translate(async () =>
        {
            var commands = RequireCommands();

            // Everything the event stream says from here on belongs to this turn.
            var baseVersion = state.Watch.Observe().Version;
            var sentAt = DateTime.UtcNow.Ticks;

            // A reviewer or prover whose report the controller refuses is asked for one correction in this same conversation: its report tool must accept it again.
            state.Evidence?.MarkSubmissionTurn(sentAt);

            JsonElement result;
            try
            {
                // No "force": a blocked agent refuses input and that refusal is reported, not overridden.
                result = await CallAsync(commands, "terminal.send", new { terminal = state.Address, text = prompt }, cancellationToken).ConfigureAwait(false);
            }
            catch (HerdrCommandException e) when (e.Kind is "blocked" or "finished" or "status_mismatch")
            {
                throw new AgentRuntimeException($"the agent did not accept the prompt ({e.Kind}): {e.Message}", e);
            }

            var herdrTurn = result.TryGetProperty("turn", out var turn) && turn.ValueKind == JsonValueKind.Number ? turn.GetInt64() : 0;

            var number = new AgentTurnNumber(++state.TurnCounter);
            state.Pending[number.Value] = new PendingTurn(herdrTurn, baseVersion, DateTimeOffset.UtcNow);

            // When the turn was sent bounds what the agent could have signalled during it (a signal is a record written by the agent's own hook, after this).
            lock (state.TurnStarts)
                state.TurnStarts.Add(sentAt);

            return number;
        }).ConfigureAwait(false);
    }

    public async Task<AgentTurnOutcome> WaitForTurnAsync(
        AgentSessionId session,
        AgentTurnNumber turn,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var state = Find(session);

        if (!state.Pending.TryGetValue(turn.Value, out var pending))
            throw new AgentRuntimeException($"turn {turn} was never sent on session {session}");

        return await Translate(async () =>
        {
            var commands = RequireCommands();
            var deadline = DateTimeOffset.UtcNow + timeout;

            while (true)
            {
                var observed = state.Watch.Observe();

                if (_hub.Lost is { } lost)
                    return new AgentTurnOutcome(AgentTurnStatus.Error, $"the event connection was lost: {lost.Message}");

                HerdrTerminal terminal;

                try
                {
                    terminal = HerdrTerminal.FromJson(
                        await CallAsync(commands, "terminal.get", new { terminal = state.Address }, cancellationToken).ConfigureAwait(false));
                }
                catch (HerdrException) when (state.Stopping)
                {
                    // The session is being stopped and its terminal is already gone: that is the stop, not a failure.
                    return new AgentTurnOutcome(AgentTurnStatus.Cancelled, "the session was stopped");
                }

                // The agent's own structured status is the authority. 'blocked' is the harness waiting for a person in its native interface: reported
                // once, then simply waited out. The first status that is not 'blocked' ends it, and a person's choice (allow, grant, deny) is not visible here.
                if (terminal.IsLive && terminal.Status?.State == "blocked")
                {
                    if (!state.InteractionReported)
                    {
                        state.InteractionReported = true;
                        return AgentTurnOutcome.AwaitingHuman(ToInteraction(state, terminal.Status));
                    }
                }
                else
                {
                    var wasWaiting = state.InteractionReported;
                    state.InteractionReported = false;

                    if (wasWaiting && terminal.Status?.State == "working")
                        return AgentTurnOutcome.Resumed;

                    if (Decide(state, pending, observed, terminal, _options.SubmissionTimeout) is { } outcome)
                        return outcome;
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    return new AgentTurnOutcome(AgentTurnStatus.Error, $"the agent did not finish its turn within {timeout}");

                await state.Watch.WaitForChangeAsync(observed.Version, remaining < CheckIn ? remaining : CheckIn, cancellationToken).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    public async Task StopSessionAsync(AgentSessionId session, CancellationToken cancellationToken = default)
    {
        SessionState? state;
        lock (_sessions)
        {
            if (!_sessions.Remove(session, out state))
                return; // not ours, or already stopped: nothing to do
        }

        state.Stopping = true;

        try
        {
            await Translate(async () =>
            {
                var commands = RequireCommands();

                await IgnoringAsync(() => CallAsync(commands, "terminal.kill", new { terminal = state.Address }, cancellationToken), "not_found", "finished").ConfigureAwait(false);
                await IgnoringAsync(() => CallAsync(commands, "terminal.rm", new { terminal = state.Address }, cancellationToken), "not_found").ConfigureAwait(false);
                _hub.Forget(state.Address);
                return 0;
            }).ConfigureAwait(false);
        }
        finally
        {
            // The session's evidence channel lives exactly as long as the session: removed whether or not the stop was clean.
            state.Evidence?.Dispose();
        }
    }

    // ---- IAgentSignalSource ----------------------------------------------------------------------------------------

    public bool CanSignal(ProfileId profile)
        => CanSignal(profile, AgentSessionPurpose.Implementation);

    public bool CanSignal(ProfileId profile, AgentSessionPurpose purpose)
        => _profiles.TryGetValue((profile, purpose), out var launch)
           && launch.Evidence is { } evidence
           && (evidence.OfferClarification || evidence.OfferWhat || evidence.OfferHow || evidence.OfferReclassification || evidence.OfferReviewRequest || evidence.OfferReview || evidence.OfferProveRequest || evidence.OfferProve);

    public Task<IReadOnlyList<AgentClarificationSignal>> CollectClarificationsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
    {
        var state = Find(session);

        if (state.Evidence is not { OffersClarification: true } channel)
            return Task.FromResult<IReadOnlyList<AgentClarificationSignal>>([]);

        var (from, before) = TurnWindow(state, session, turn);

        return Task.FromResult(channel.CollectClarifications(from, before));
    }

    public Task<IReadOnlyList<AgentWhatSignal>> CollectWhatProposalsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
    {
        var state = Find(session);

        if (state.Evidence is not { OffersWhat: true } channel)
            return Task.FromResult<IReadOnlyList<AgentWhatSignal>>([]);

        var (from, before) = TurnWindow(state, session, turn);

        return Task.FromResult(channel.CollectWhatProposals(from, before));
    }

    public Task<IReadOnlyList<AgentHowSignal>> CollectHowProposalsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
    {
        var state = Find(session);

        if (state.Evidence is not { OffersHow: true } channel)
            return Task.FromResult<IReadOnlyList<AgentHowSignal>>([]);

        var (from, before) = TurnWindow(state, session, turn);

        return Task.FromResult(channel.CollectHowProposals(from, before));
    }

    public Task<IReadOnlyList<AgentReclassificationSignal>> CollectReclassificationsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
    {
        var state = Find(session);

        if (state.Evidence is not { OffersReclassification: true } channel)
            return Task.FromResult<IReadOnlyList<AgentReclassificationSignal>>([]);

        var (from, before) = TurnWindow(state, session, turn);

        return Task.FromResult(channel.CollectReclassifications(from, before));
    }

    public Task<IReadOnlyList<AgentReviewRequestSignal>> CollectReviewRequestsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
    {
        var state = Find(session);

        if (state.Evidence is not { OffersReviewRequest: true } channel)
            return Task.FromResult<IReadOnlyList<AgentReviewRequestSignal>>([]);

        var (from, before) = TurnWindow(state, session, turn);

        return Task.FromResult(channel.CollectReviewRequests(from, before));
    }

    public Task<IReadOnlyList<AgentReviewSubmission>> CollectReviewSubmissionsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
    {
        var state = Find(session);

        if (state.Evidence is not { OffersReview: true } channel)
            return Task.FromResult<IReadOnlyList<AgentReviewSubmission>>([]);

        var (from, before) = TurnWindow(state, session, turn);

        return Task.FromResult(channel.CollectReviewSubmissions(from, before));
    }

    public Task<IReadOnlyList<AgentProveRequestSignal>> CollectProveRequestsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
    {
        var state = Find(session);

        if (state.Evidence is not { OffersProveRequest: true } channel)
            return Task.FromResult<IReadOnlyList<AgentProveRequestSignal>>([]);

        var (from, before) = TurnWindow(state, session, turn);

        return Task.FromResult(channel.CollectProveRequests(from, before));
    }

    public Task<IReadOnlyList<AgentProofSubmission>> CollectProofSubmissionsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
    {
        var state = Find(session);

        if (state.Evidence is not { OffersProve: true } channel)
            return Task.FromResult<IReadOnlyList<AgentProofSubmission>>([]);

        var (from, before) = TurnWindow(state, session, turn);

        return Task.FromResult(channel.CollectProofSubmissions(from, before));
    }

    /// <summary>The span of time (ticks) the given turn of the session occupied: from when it was sent until the next turn was sent (or, for the latest, until now).</summary>
    private static (long From, long? Before) TurnWindow(SessionState state, AgentSessionId session, AgentTurnNumber turn)
    {
        lock (state.TurnStarts)
        {
            if (turn.Value > state.TurnStarts.Count)
                throw new AgentRuntimeException($"turn {turn} was never sent on session {session}");

            return (state.TurnStarts[turn.Value - 1], turn.Value < state.TurnStarts.Count ? state.TurnStarts[turn.Value] : null);
        }
    }

    // ---- IExecutionEvidenceSource ----------------------------------------------------------------------------------

    public Task BeginTurnAsync(AgentSessionId session, CancellationToken cancellationToken = default)
    {
        Find(session).Evidence?.MarkTurnStart();

        return Task.CompletedTask;
    }

    public Task<ObservedTurnEvidence?> CollectTurnAsync(AgentSessionId session, CancellationToken cancellationToken = default)
        => Task.FromResult(Find(session).Evidence is { ObservesExecutions: true } channel ? channel.Collect() : null);

    /// <summary>
    /// The evidence channel of a session this runtime owns, or null when the session has none. Exposed for diagnostics and tests
    /// (for example the Claude session id and the handler timings); the controller only ever uses <see cref="IExecutionEvidenceSource"/>.
    /// </summary>
    public ClaudeEvidenceChannel? EvidenceChannelOf(AgentSessionId session)
        => Find(session).Evidence;

    private static string PurposeText(AgentSessionPurpose purpose) => purpose switch
    {
        AgentSessionPurpose.Review => "an independent review",
        AgentSessionPurpose.Prove => "an independent proof",
        _ => "implementation",
    };

    private static ClaudeEvidenceChannel? OpenEvidenceChannel(AgentLaunchProfile launch, AgentSessionRequest request, string? conversationReference, out string? failure)
    {
        failure = null;

        if (launch.Evidence is not { } options || (request.ObservableChecks.Count == 0 && !options.OfferClarification && !options.OfferWhat && !options.OfferHow && !options.OfferReclassification && !options.OfferReviewRequest && !options.OfferReview && !options.OfferProveRequest && !options.OfferProve))
        {
            failure = "no channel is configured for this session";
            return null;
        }

        // The channel is bound to the conversation's own id, so a resumed conversation keeps producing evidence (and, if the agent asks, questions) for exactly itself.
        return ClaudeEvidenceChannel.TryCreate(options, request.WorkspacePath, request.ObservableChecks, conversationReference, options.OfferClarification, options.OfferWhat, options.OfferHow, options.OfferReclassification, options.OfferReviewRequest, options.OfferReview, options.OfferProveRequest, options.OfferProve, out failure);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        SessionState[] remaining;
        lock (_sessions)
            remaining = _sessions.Values.ToArray();

        foreach (var session in remaining)
            await StopQuietlyAsync(session).ConfigureAwait(false);

        if (_commands is not null)
            await _commands.DisposeAsync().ConfigureAwait(false);

        if (_events is not null)
            await _events.DisposeAsync().ConfigureAwait(false);

        if (_options.StopCoreOnDispose && _coreStartedByThisRuntime)
        {
            using var budget = new CancellationTokenSource(_options.CommandTimeout);
            try
            {
                await _server.StopAsync(budget.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HerdrException or OperationCanceledException)
            {
                // best effort: the core is isolated and has no terminals of ours left
            }
        }

        _connectLock.Dispose();
    }

    // ---- outcome rule ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The structured turn rule. <c>turn</c> counts every period that ended 'working' (a block counts too), so after a send at turn T the agent's
    /// turn is over when the terminal reports a turn above T and says done or idle, never merely because the counter moved. An errored status, or
    /// the process ending, is an outcome of its own. A 'blocked' status is not decided here (see <see cref="WaitForTurnAsync"/>). A stale
    /// <c>done</c> from the previous turn is never taken as this turn's.
    /// </summary>
    private static AgentTurnOutcome? Decide(SessionState state, PendingTurn pending, TerminalObservation observed, HerdrTerminal terminal, TimeSpan submissionTimeout)
    {
        var status = terminal.Status;
        var word = status?.State;

        if (!terminal.IsLive || observed.Finished)
        {
            if (state.Stopping)
                return new AgentTurnOutcome(AgentTurnStatus.Cancelled, "the session was stopped");

            return new AgentTurnOutcome(
                AgentTurnStatus.Error,
                $"the agent process ended ({terminal.Reason ?? observed.FinishedReason ?? "unknown reason"}, exit code {(terminal.ExitCode ?? observed.ExitCode)?.ToString() ?? "none"})");
        }

        if (word == "error")
            return new AgentTurnOutcome(AgentTurnStatus.Error, Describe(status!));

        var turnAdvanced = terminal.Turn > pending.HerdrTurn;
        var sawWorking = word == "working" || observed.SawStatusSince(pending.BaseVersion, "working");

        // The turn counter moved past the send and the agent is not working: its turn is over.
        if (turnAdvanced && word is "done" or "idle")
            return AgentTurnOutcome.Done;

        // It was seen working after the send and now reports done (the turn event may still be in flight).
        if (sawWorking && word == "done")
            return AgentTurnOutcome.Done;

        // Sent, yet the agent never started a turn: it never reported working and the turn counter did not move. The prompt was not taken (for example it was pasted into the
        // agent's input but not submitted), so waiting longer would only hang. Reported as a failure of the turn; nothing is typed again.
        if (!sawWorking && !turnAdvanced && word == "idle" && DateTimeOffset.UtcNow - pending.SentAt > submissionTimeout)
            return new AgentTurnOutcome(AgentTurnStatus.Error, $"the agent did not start working on the prompt within {submissionTimeout} (it stayed idle); the prompt was not taken");

        return null;
    }

    /// <summary>
    /// What the harness itself says it is waiting for. <c>kind</c> and <c>msg</c> come from the agent's own structured status (Claude Code reports
    /// <c>permission</c> with a line such as "approve Bash: dotnet --list-sdks"); nothing is read from the screen.
    /// </summary>
    private AgentInteraction ToInteraction(SessionState state, HerdrStatus status)
    {
        var kind = status.Kind switch
        {
            "permission" => AgentInteractionKind.Permission,
            "question" => AgentInteractionKind.Question,
            "auth" => AgentInteractionKind.Authentication,
            _ => AgentInteractionKind.Unspecified,
        };

        var description = string.IsNullOrWhiteSpace(status.Message) ? "the agent is blocked and did not say why" : status.Message;

        return new AgentInteraction(kind, description, new NativeAttachTarget(_options.Session, state.Id.Value));
    }

    private static string Describe(HerdrStatus status)
        => string.Join(": ", new[] { status.Kind, status.Message }.Where(part => !string.IsNullOrWhiteSpace(part)).DefaultIfEmpty($"status '{status.State}'"));

    // ---- readiness -------------------------------------------------------------------------------------------------

    /// <param name="resuming">Set when this launch re-enters an existing conversation: an exit before any status is then checked for "conversation not found".</param>
    /// <param name="interactions">Set only when a person is attending. The agent waiting for them during startup is then reported and waited out (never answered) instead of failing the start.</param>
    private async Task WaitUntilReadyAsync(HerdrRpcConnection commands, SessionState session, IAgentConversationLaunch? resuming, IAgentStartupInteractions? interactions, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _options.StartupTimeout;
        DateTimeOffset? personDeadline = null;

        while (true)
        {
            var observed = session.Watch.Observe();

            var terminal = HerdrTerminal.FromJson(
                await CallAsync(commands, "terminal.get", new { terminal = session.Address }, cancellationToken).ConfigureAwait(false));

            if (!terminal.IsLive)
            {
                if (resuming is not null && session.Conversation is { } conversation)
                    await ThrowIfConversationMissingAsync(commands, session, terminal, resuming, conversation, cancellationToken).ConfigureAwait(false);

                throw new AgentStartupBlockedException($"the agent exited during startup ({terminal.Reason ?? "unknown reason"}, exit code {terminal.ExitCode?.ToString() ?? "none"})");
            }

            var word = terminal.Status?.State;

            if (word == "error")
                throw new AgentRuntimeException($"the agent reported an error during startup: {Describe(terminal.Status!)}");

            if (word is not null && _options.ReadyStatuses.Contains(word))
            {
                if (personDeadline is not null && interactions is not null)
                    await interactions.ClosedAsync(cancellationToken).ConfigureAwait(false);

                return;
            }

            var blocked = word == "blocked";
            var quiet = !blocked && DateTimeOffset.UtcNow >= deadline;

            if (blocked || quiet)
            {
                // Nobody is attending: a startup that needs a person fails, and nothing is answered.
                if (interactions is null)
                {
                    throw new AgentStartupBlockedException(
                        blocked
                            ? $"the agent is blocked at startup ({Describe(terminal.Status!)}); this runtime never answers dialogs"
                            : $"the agent reported no structured ready status within {_options.StartupTimeout.TotalSeconds:0} seconds (last status '{word ?? "none"}'). "
                              + "A dialog such as workspace trust, login or an update prompt may be waiting; it was not answered.");
                }

                if (personDeadline is null)
                {
                    personDeadline = DateTimeOffset.UtcNow + _options.StartupInteractionTimeout;

                    // What the harness says it is waiting for, or, when it reports nothing at all, only the fact that it has not said it is ready. Nothing is read from the screen.
                    var interaction = blocked
                        ? ToInteraction(session, terminal.Status!)
                        : new AgentInteraction(
                            AgentInteractionKind.Unspecified,
                            $"the agent has not reported ready within {_options.StartupTimeout.TotalSeconds:0} seconds; a first-run question, such as trusting this folder, may be waiting in its own session",
                            new NativeAttachTarget(_options.Session, session.Id.Value));

                    await interactions.OpenedAsync(interaction, cancellationToken).ConfigureAwait(false);
                }
                else if (DateTimeOffset.UtcNow >= personDeadline)
                {
                    throw new AgentStartupBlockedException($"the agent was still waiting for a person after {_options.StartupInteractionTimeout.TotalMinutes:0} minutes at startup; it was not answered");
                }

                await session.Watch.WaitForChangeAsync(observed.Version, CheckIn, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Not waiting for anyone (any more): a blocked status that cleared is the person's answer, and the agent gets its normal time to become ready.
            if (personDeadline is not null && interactions is not null)
            {
                personDeadline = null;
                await interactions.ClosedAsync(cancellationToken).ConfigureAwait(false);
                deadline = DateTimeOffset.UtcNow + _options.StartupTimeout;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;

            await session.Watch.WaitForChangeAsync(observed.Version, remaining < CheckIn ? remaining : CheckIn, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A resumed agent that exits with a failure before it ever reported a status is either a conversation that does not exist or an ordinary startup
    /// failure. Herdr's structured facts cannot tell them apart, so, only in that exact situation, the program's last words are checked for its own
    /// "no such conversation" message naming exactly this reference. Anything else (another message, an unreadable screen, a different exit) is left to
    /// the caller as the startup failure it is: nothing falls back on a guess.
    /// </summary>
    private async Task ThrowIfConversationMissingAsync(
        HerdrRpcConnection commands,
        SessionState session,
        HerdrTerminal terminal,
        IAgentConversationLaunch launch,
        AgentConversationRef conversation,
        CancellationToken cancellationToken)
    {
        if (terminal.ExitCode is null or 0 || terminal.Status is not null)
            return;

        string output;

        try
        {
            var read = await CallAsync(commands, "terminal.read", new { terminal = session.Address, lines = 20 }, cancellationToken).ConfigureAwait(false);
            output = read.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? string.Empty : string.Empty;
        }
        catch (HerdrException)
        {
            return;
        }

        if (launch.IndicatesMissingConversation(output, conversation.Reference))
            throw new AgentConversationNotFoundException("the agent reports that the native conversation does not exist");
    }

    // ---- connections -----------------------------------------------------------------------------------------------

    private async Task<HerdrRpcConnection> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_commands is { IsLost: false } existing && _events is { IsLost: false })
                return existing;

            if (_commands is not null || _events is not null)
                throw new AgentRuntimeException("the connection to the core was lost; start a new runtime to continue");

            // CLI: only to start the isolated core (the pipe API cannot).
            await _server.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            _coreStartedByThisRuntime = true;

            _events = await HerdrRpcConnection.ConnectAsync(await _transports.OpenAsync(cancellationToken).ConfigureAwait(false), _options.CommandTimeout, cancellationToken).ConfigureAwait(false);
            _events.NotificationReceived += _hub.Handle;
            _events.Lost += _hub.MarkLost;

            // Dedicated connection: it carries only notifications after this call, so it never shares a stream with requests.
            await _events.CallAsync(
                "events.subscribe",
                new { labels = new Dictionary<string, string> { ["aw_runtime"] = _marker } },
                _options.CommandTimeout,
                cancellationToken).ConfigureAwait(false);

            _commands = await HerdrRpcConnection.ConnectAsync(await _transports.OpenAsync(cancellationToken).ConfigureAwait(false), _options.CommandTimeout, cancellationToken).ConfigureAwait(false);

            return _commands;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private HerdrRpcConnection RequireCommands()
        => _commands is { IsLost: false } commands
            ? commands
            : throw new AgentRuntimeException("there is no live connection to the core");

    private Task<JsonElement> CallAsync(HerdrRpcConnection connection, string method, object parameters, CancellationToken cancellationToken)
        => connection.CallAsync(method, parameters, _options.CommandTimeout, cancellationToken);

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private SessionState Find(AgentSessionId session)
    {
        ThrowIfDisposed();

        lock (_sessions)
        {
            return _sessions.TryGetValue(session, out var state)
                ? state
                : throw new AgentRuntimeException($"session {session} is not running in this runtime");
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private async Task StopQuietlyAsync(SessionState session)
    {
        using var budget = new CancellationTokenSource(_options.CommandTimeout);
        try
        {
            await StopSessionAsync(session.Id, budget.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AgentRuntimeException or OperationCanceledException)
        {
            // already being torn down
        }
    }

    private static async Task IgnoringAsync(Func<Task<JsonElement>> call, params string[] kinds)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (HerdrCommandException e) when (e.Kind is not null && kinds.Contains(e.Kind))
        {
            // expected: the terminal is already gone
        }
    }

    /// <summary>Herdr failures cross the port as <see cref="AgentRuntimeException"/>; cancellation and port errors pass through.</summary>
    private static async Task<T> Translate<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (HerdrException e)
        {
            throw new AgentRuntimeException(e.Message, e);
        }
    }

    private static string Sanitize(string value)
    {
        var cleaned = new string(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
        return cleaned.Length <= 60 ? cleaned : cleaned[..60];
    }

    private sealed record PendingTurn(long HerdrTurn, long BaseVersion, DateTimeOffset SentAt);

    private sealed class SessionState(AgentSessionId id, string address, TerminalWatch watch)
    {
        public AgentSessionId Id { get; } = id;

        public string Address { get; } = address;

        public TerminalWatch Watch { get; } = watch;

        public int TurnCounter { get; set; }

        public bool Stopping { get; set; }

        /// <summary>True while a native interaction was already reported to the caller and the agent has not left 'blocked' yet.</summary>
        public bool InteractionReported { get; set; }

        public Dictionary<int, PendingTurn> Pending { get; } = [];

        /// <summary>When each turn was sent (UTC ticks), indexed by turn number minus one.</summary>
        public List<long> TurnStarts { get; } = [];

        public ClaudeEvidenceChannel? Evidence { get; init; }

        /// <summary>The native conversation this session runs, when the profile can name one.</summary>
        public AgentConversationRef? Conversation { get; init; }
    }
}

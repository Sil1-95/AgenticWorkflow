using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Application.Execution;

// Native conversation continuity. A process is not a conversation: the agent's own conversation can outlive the process that hosted it, and a runtime
// that can reattach to it lets the controller continue the SAME conversation instead of restating the task to a stranger. Nothing here knows Claude,
// Herdr, transcripts or resume flags: a runtime hands out an opaque reference, the controller keeps it, and only that runtime can read it.

/// <summary>
/// An opaque, provider-neutral reference to one native agent conversation. <see cref="Profile"/> is the workflow profile the conversation was started
/// under and <see cref="Runtime"/> names the kind of runtime that issued it; <see cref="Reference"/> means something only to that runtime. It carries no
/// secret and no path, and the controller never interprets it.
/// </summary>
public sealed record AgentConversationRef
{
    public const int MaxLength = 200;

    public AgentConversationRef(ProfileId profile, string runtime, string reference)
    {
        ArgumentNullException.ThrowIfNull(profile);

        Profile = profile;
        Runtime = Required(runtime, nameof(runtime));
        Reference = Required(reference, nameof(reference));
    }

    public ProfileId Profile { get; }

    /// <summary>The kind of runtime that issued the reference (for example <c>claude-code</c>); a different kind of runtime must refuse it.</summary>
    public string Runtime { get; }

    /// <summary>Opaque to everything but the issuing runtime.</summary>
    public string Reference { get; }

    private static string Required(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLength || value.Any(char.IsControl))
            throw new ArgumentException($"A conversation reference needs a non-blank, bounded, single-line {name}.", name);

        return value;
    }
}

/// <summary>
/// A conversation reference bound to the run it belongs to, as persisted. A run only ever resumes the conversation recorded for that run
/// (the binding is checked on every read, not assumed).
/// </summary>
public sealed record AgentConversationRecord(TaskId Task, AgentConversationRef Conversation);

/// <summary>
/// Optional capability of an <see cref="IAgentRuntime"/> (the same pattern as <c>IExecutionEvidenceSource</c>): it can name the native conversation of a
/// session it started and later attach a new session to that same conversation. A runtime without it simply does not implement the interface, and the
/// controller falls back to a fresh session, explicitly.
/// </summary>
public interface IAgentConversationRuntime
{
    /// <summary>True when this runtime can resume conversations that belong to <paramref name="profile"/>.</summary>
    bool CanResume(ProfileId profile);

    /// <summary>The conversation behind a session this runtime started, or null when it has none it can later resume.</summary>
    AgentConversationRef? ConversationOf(AgentSessionId session);

    /// <summary>
    /// Starts a NEW session attached to the existing native conversation <paramref name="conversation"/> and returns once it is ready for a turn. The
    /// session is launched with exactly the configuration of <see cref="AgentSessionRequest.Profile"/> (the hard limits are not remembered by the
    /// conversation; they are applied again). Throws <see cref="AgentConversationNotFoundException"/> only when the runtime knows the conversation does
    /// not exist, <see cref="InvalidAgentConversationException"/> for a reference this runtime refuses, and any other <see cref="AgentRuntimeException"/>
    /// for everything else (which must never be mistaken for "not found").
    /// </summary>
    Task<AgentSessionId> ResumeSessionAsync(AgentSessionRequest request, AgentConversationRef conversation, CancellationToken cancellationToken = default);
}

/// <summary>
/// Where the controller keeps conversation references between processes. One conversation per run. Implementations decide the medium; they must
/// never return another run's reference and must report anything they cannot trust as <see cref="InvalidAgentConversationException"/>.
/// </summary>
public interface IAgentConversationStore
{
    /// <summary>The run's recorded conversation, or null when none was recorded (a run that predates this, or whose session had no resumable conversation).</summary>
    Task<AgentConversationRecord?> FindAsync(TaskId task, CancellationToken cancellationToken = default);

    /// <summary>Records <paramref name="conversation"/> as the run's conversation, replacing any earlier one.</summary>
    Task SaveAsync(AgentConversationRecord record, CancellationToken cancellationToken = default);
}

/// <summary>The runtime positively determined that the native conversation does not exist (any more). The only resume failure that may fall back to a fresh session.</summary>
public sealed class AgentConversationNotFoundException : AgentRuntimeException
{
    public AgentConversationNotFoundException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>A conversation reference is malformed, belongs to another run, profile or runtime, or cannot be trusted. Never falls back: it is an error.</summary>
public sealed class InvalidAgentConversationException : AgentRuntimeException
{
    public InvalidAgentConversationException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>How the agent conversation of a run stretch came about. Observable provenance, never workflow state.</summary>
public enum AgentContinuationKind
{
    /// <summary>A brand-new conversation (the first stretch of a run).</summary>
    NewConversation,

    /// <summary>The earlier native conversation was resumed: the agent remembers the task and its own work.</summary>
    ResumedNative,

    /// <summary>A fresh session with no memory of the earlier one; <see cref="AgentContinuation.Reason"/> says why that was the only option.</summary>
    FreshSession,

    /// <summary>Resuming was attempted and failed for a reason that is not "the conversation does not exist". Nothing fell back.</summary>
    ResumeFailed,
}

/// <summary>Why a continuation was fresh rather than resumed (stable codes, shown to the person).</summary>
public static class AgentContinuationReasons
{
    /// <summary>The runtime does not advertise resume for this profile.</summary>
    public const string ResumeUnsupported = "resume_unsupported";

    /// <summary>No conversation was recorded for the run (it predates native continuity, or its session had none).</summary>
    public const string NoConversationRecorded = "no_conversation_recorded";

    /// <summary>The runtime determined the recorded conversation no longer exists.</summary>
    public const string ConversationNotFound = "conversation_not_found";
}

/// <summary>How the agent's conversation was obtained for a stretch of a run, and, when fresh, why.</summary>
public sealed record AgentContinuation(AgentContinuationKind Kind, string? Reason = null)
{
    public static AgentContinuation New { get; } = new(AgentContinuationKind.NewConversation);

    public static AgentContinuation Resumed { get; } = new(AgentContinuationKind.ResumedNative);
}

/// <summary>The optional continuity ports a run may use together: the runtime that can resume, and where the references live.</summary>
public sealed record AgentConversationSupport(IAgentConversationRuntime Runtime, IAgentConversationStore Store);

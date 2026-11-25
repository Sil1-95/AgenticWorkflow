namespace AgenticWorkflow.Runtime.Herdr;

/// <summary>
/// How one kind of agent program names and re-enters its own native conversation on its command line. Adapter-private knowledge: the controller sees
/// only an opaque reference and never a flag, a path or a file format. A profile without one cannot resume.
/// </summary>
public interface IAgentConversationLaunch
{
    /// <summary>The kind of runtime this launch belongs to; it is the <c>Runtime</c> of every reference it issues, and a different kind must be refused.</summary>
    string Runtime { get; }

    /// <summary>A new, unique native conversation reference, chosen before launch so it never has to be discovered afterwards.</summary>
    string NewReference();

    /// <summary>True for exactly the references this launch issues. Anything else is refused before a process is started.</summary>
    bool IsValidReference(string reference);

    /// <summary><paramref name="argv"/> (the profile's fixed launch, including its hard limits) that starts a new conversation named <paramref name="reference"/>.</summary>
    IReadOnlyList<string> ForNewConversation(IReadOnlyList<string> argv, string reference);

    /// <summary><paramref name="argv"/> (the same fixed launch, so the hard limits apply again) that re-enters the existing conversation <paramref name="reference"/>.</summary>
    IReadOnlyList<string> ForResume(IReadOnlyList<string> argv, string reference);

    /// <summary>
    /// True when the exited program's last words say, about exactly <paramref name="reference"/>, that no such conversation exists. Consulted only after
    /// the program has already exited with a failure before reporting any status, and only to tell "not found" from every other startup failure; it
    /// decides nothing else. Anything it does not recognise is not "not found".
    /// </summary>
    bool IndicatesMissingConversation(string finalOutput, string reference);
}

/// <summary>
/// Claude Code's own public conversation flags: <c>--session-id &lt;uuid&gt;</c> names a new conversation up front, <c>--resume &lt;uuid&gt;</c> re-enters
/// it from a fresh process (same conversation, same transcript, no fork). Claude treats a non-UUID <c>--resume</c> value as a search term and opens an
/// interactive picker, so only canonical UUIDs are ever passed. Nothing about its transcript files or storage is touched.
/// </summary>
public sealed class ClaudeConversationLaunch : IAgentConversationLaunch
{
    public string Runtime => ClaudeCodeLaunch.AppName;

    public string NewReference() => Guid.NewGuid().ToString();

    public bool IsValidReference(string reference)
        => Guid.TryParseExact(reference, "D", out var parsed) && string.Equals(parsed.ToString(), reference, StringComparison.Ordinal);

    public IReadOnlyList<string> ForNewConversation(IReadOnlyList<string> argv, string reference)
        => Insert(argv, "--session-id", reference);

    public IReadOnlyList<string> ForResume(IReadOnlyList<string> argv, string reference)
        => Insert(argv, "--resume", reference);

    public bool IndicatesMissingConversation(string finalOutput, string reference)
        => finalOutput.Contains($"No conversation found with session ID: {reference}", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> Insert(IReadOnlyList<string> argv, string flag, string reference)
        => [argv[0], flag, reference, .. argv.Skip(1)];
}

using System.Globalization;

namespace AgenticWorkflow.Cli.Reporting;

internal static class Format
{
    /// <summary>The workflow state as people read it: <c>RUNNING</c>, <c>AWAITING_HUMAN</c>.</summary>
    public static string State(AgenticWorkflow.Core.Lifecycle.WorkflowState state)
        => state == AgenticWorkflow.Core.Lifecycle.WorkflowState.AwaitingHuman ? "AWAITING_HUMAN" : state.ToString().ToUpperInvariant();

    /// <summary>Text that came from the agent's own tool input is never echoed with control characters (terminal escape sequences).</summary>
    public static string Printable(string text)
        => new(text.Select(c => char.IsControl(c) ? '?' : c).ToArray());

    /// <summary>
    /// Multi-line text that came from an agent's tool input, indented under a label: line breaks and tabs are kept (a WHAT section is prose), every other control
    /// character (a terminal escape sequence) is shown as <c>?</c>.
    /// </summary>
    public static string Block(string text, string indent)
    {
        var printable = new string(text.Select(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t') ? '?' : c).ToArray());

        return string.Join(Environment.NewLine, printable.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line => indent + line.TrimEnd()));
    }

    /// <summary>
    /// <paramref name="text"/> on one line (whitespace collapsed), with other control characters shown as <c>?</c> and cut to <paramref name="limit"/> characters
    /// (with <c>...</c>). Empty when there is nothing to show. For text that comes from an agent's own tool input and is only ever display text.
    /// </summary>
    public static string Bounded(string? text, int limit)
    {
        var printable = new string((text ?? string.Empty).Select(c => char.IsControl(c) && !char.IsWhiteSpace(c) ? '?' : c).ToArray());
        var singleLine = string.Join(' ', printable.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return singleLine.Length <= limit ? singleLine : singleLine[..(limit - 3)] + "...";
    }

    /// <summary>The first line of <paramref name="text"/>, printable and cut to <paramref name="maxLength"/> characters (with <c>...</c>), so a long or multi-line text stays one short line.</summary>
    public static string Excerpt(string text, int maxLength)
    {
        var line = Printable(text.Trim().Split('\n')[0].Trim());

        return line.Length <= maxLength ? line : line[..(maxLength - 3)] + "...";
    }

    /// <summary>The first eight characters of a run id, enough to tell runs apart in a short message.</summary>
    public static string ShortRunId(AgenticWorkflow.Core.Identity.TaskId runId) => runId.ToString()[..8];

    /// <summary><c>850ms</c>, <c>4.2s</c>, <c>1m55s</c>, <c>1h02m</c>.</summary>
    public static string Duration(TimeSpan value)
    {
        if (value < TimeSpan.FromSeconds(1))
            return string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalMilliseconds}ms");

        if (value < TimeSpan.FromMinutes(1))
            return string.Create(CultureInfo.InvariantCulture, $"{value.TotalSeconds:F1}s");

        if (value < TimeSpan.FromHours(1))
            return string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalMinutes}m{value.Seconds:D2}s");

        return string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours}h{value.Minutes:D2}m");
    }

    /// <summary>The first <paramref name="length"/> hex characters of a fingerprint such as <c>git-worktree-sha256:3f9a...</c>.</summary>
    public static string ShortFingerprint(string fingerprint, int length = 12)
    {
        var digest = fingerprint.Contains(':') ? fingerprint[(fingerprint.LastIndexOf(':') + 1)..] : fingerprint;

        return digest.Length <= length ? digest : digest[..length];
    }
}

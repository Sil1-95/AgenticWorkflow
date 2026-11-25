namespace AgenticWorkflow.Application.Evidence;

/// <summary>
/// Deliberately tiny, conservative reading of a shell command line, used only to decide whether the agent's command is
/// <em>obviously</em> the same process invocation as a configured check. It never interprets shell semantics: a command is
/// recognised only when it consists of plain words (letters, digits and <c>_ - . / : = + , @</c>) separated by spaces. Quotes,
/// escapes, expansions, globs, redirections, pipes, separators, chaining, environment prefixes, subshells and newlines are all
/// "not recognised", which only ever means the controller runs the check itself. The one extension is the value of <c>--filter</c>,
/// which may contain a .NET test filter's <c>!=</c>, <c>!~</c> and <c>~</c> operators (see <see cref="IsPlainFilterExpression"/>).
/// </summary>
public static class CommandForm
{
    public const int MaxCommandLength = 2000;

    private const string FilterOption = "--filter";

    private static readonly HashSet<string> ReadOnlyGitSubcommands =
        new(StringComparer.Ordinal) { "status", "diff", "log", "show", "grep", "ls-files", "rev-parse" };

    private static readonly HashSet<string> ReadOnlyPrograms =
        new(StringComparer.OrdinalIgnoreCase) { "ls", "pwd", "cat", "head", "tail", "wc", "echo" };

    /// <summary>The words of a plain command, or null when the command is not a plain word list.</summary>
    public static IReadOnlyList<string>? Tokenize(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > MaxCommandLength)
        {
            return null;
        }

        var tokens = new List<string>();

        foreach (var word in command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var isFilterExpression = tokens.Count > 0 && tokens[^1] == FilterOption;

            if (!(isFilterExpression ? IsPlainFilterExpression(word) : word.All(IsPlainWordCharacter)))
            {
                return null;
            }

            tokens.Add(word);
        }

        return tokens.Count == 0 ? null : tokens;
    }

    /// <summary>True when <paramref name="command"/> is exactly <paramref name="executable"/> followed by exactly <paramref name="arguments"/>.</summary>
    public static bool Matches(string executable, IReadOnlyList<string> arguments, string? command)
    {
        var tokens = Tokenize(command);

        if (tokens is null || tokens.Count != arguments.Count + 1)
        {
            return false;
        }

        if (!string.Equals(tokens[0], executable, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var i = 0; i < arguments.Count; i++)
        {
            if (!string.Equals(tokens[i + 1], arguments[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The canonical text recorded as a command's identity (executable then arguments, single-space separated).</summary>
    public static string Identity(string executable, IReadOnlyList<string> arguments)
        => string.Join(' ', [executable, .. arguments]);

    /// <summary>
    /// True for a recognised command that cannot change the workspace: a read-only Git subcommand without an output option, or a
    /// plain read-only program. Anything not in this short list counts as possibly changing state.
    /// </summary>
    public static bool IsKnownReadOnly(string? command)
    {
        var tokens = Tokenize(command);

        if (tokens is null)
        {
            return false;
        }

        if (string.Equals(tokens[0], "git", StringComparison.OrdinalIgnoreCase))
        {
            return tokens.Count >= 2
                   && ReadOnlyGitSubcommands.Contains(tokens[1])
                   && !tokens.Skip(2).Any(t => t.StartsWith("--output", StringComparison.Ordinal) || t == "-o");
        }

        return ReadOnlyPrograms.Contains(tokens[0]);
    }

    /// <summary>Same directory after normalisation (case-insensitive, trailing separators ignored). Either side blank means unknown, so false.</summary>
    public static bool SameDirectory(string? actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(actual).TrimEnd('/', '\\'),
                Path.GetFullPath(expected).TrimEnd('/', '\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The directory a check runs in: the workspace root, or the workspace-relative directory it names.</summary>
    public static string ExpectedDirectory(string workspacePath, string? relativeWorkingDirectory)
        => string.IsNullOrWhiteSpace(relativeWorkingDirectory)
            ? Path.GetFullPath(workspacePath)
            : Path.GetFullPath(Path.Combine(workspacePath, relativeWorkingDirectory));

    /// <summary>
    /// The value of a <c>--filter</c> option (a .NET test filter such as <c>FullyQualifiedName!~Slow</c>): a plain word that may also
    /// contain the filter's own <c>!=</c>, <c>!~</c> and <c>~</c> operators. Those are only data here because of where they sit: a
    /// <c>~</c> is a tilde expansion only at the start of a word or right after <c>=</c> or <c>:</c>, and a <c>!</c> is only special
    /// (history expansion, pipeline negation) as, or at the start of, a word. So neither may start the word or follow <c>=</c> or
    /// <c>:</c>, and <c>!</c> must be followed by <c>=</c> or <c>~</c>. Every character that composes commands (<c>&amp; | ; &lt; &gt; ( ) $ `</c>,
    /// quotes, backslash, whitespace) stays unrecognised, quoted or not, so the word cannot run anything.
    /// </summary>
    private static bool IsPlainFilterExpression(string word)
    {
        for (var i = 0; i < word.Length; i++)
        {
            var c = word[i];

            if (IsPlainWordCharacter(c))
            {
                continue;
            }

            if (c is not ('!' or '~') || i == 0 || word[i - 1] is '=' or ':')
            {
                return false;
            }

            if (c == '!' && (i + 1 >= word.Length || word[i + 1] is not ('=' or '~')))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPlainWordCharacter(char c)
        => c is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_' or '-' or '.' or '/' or ':' or '=' or '+' or ',' or '@';
}

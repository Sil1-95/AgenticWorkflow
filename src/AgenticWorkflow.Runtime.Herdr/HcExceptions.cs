namespace AgenticWorkflow.Runtime.Herdr;

/// <summary>Base type for every failure raised while talking to Herdr.</summary>
public abstract class HerdrException : Exception
{
    protected HerdrException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>The <c>hc</c> executable could not be started.</summary>
public sealed class HcNotFoundException : HerdrException
{
    public HcNotFoundException(string executable, Exception innerException)
        : base($"Could not start '{executable}'. Is herdr-core installed and on PATH?", innerException)
    {
    }
}

/// <summary>A command did not finish inside its time budget; the process was killed.</summary>
public sealed class HcTimeoutException : HerdrException
{
    public HcTimeoutException(string executable, IReadOnlyList<string> arguments, TimeSpan timeout)
        : base($"'{executable} {string.Join(' ', arguments)}' did not finish within {timeout}.")
    {
    }
}

/// <summary>Herdr reported a structured error (<c>{"error":{kind,message,hint}}</c>) or exited non-zero.</summary>
public sealed class HerdrCommandException : HerdrException
{
    public HerdrCommandException(int exitCode, string? kind, string message, string? hint)
        : base($"Herdr error (exit {exitCode}, kind '{kind ?? "unknown"}'): {message}")
    {
        ExitCode = exitCode;
        Kind = kind;
        Hint = hint;
    }

    public int ExitCode { get; }

    public string? Kind { get; }

    public string? Hint { get; }
}

/// <summary>Herdr produced output that was not the JSON the caller expected.</summary>
public sealed class HerdrProtocolException : HerdrException
{
    public HerdrProtocolException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

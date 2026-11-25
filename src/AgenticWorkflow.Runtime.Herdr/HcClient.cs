using System.Globalization;
using System.Text.Json;

namespace AgenticWorkflow.Runtime.Herdr;

/// <summary>
/// Thin typed wrapper over the <c>hc</c> CLI. Every call uses <c>--json</c> and an explicit
/// argument list; failures surface as <see cref="HerdrException"/> subclasses.
/// </summary>
internal sealed class HcClient
{
    private static readonly TimeSpan WaitProcessMargin = TimeSpan.FromSeconds(15);

    private readonly HcClientOptions _options;
    private readonly HcProcessRunner _runner;

    public HcClient(HcClientOptions? options = null)
    {
        _options = options ?? new HcClientOptions();

        var environment = new Dictionary<string, string>();

        if (_options.Session is not null)
            environment["HC_SESSION"] = _options.Session;

        _runner = new HcProcessRunner(_options.Executable, environment, scrubbedEnvironmentPrefixes: _options.ScrubbedEnvironmentPrefixes);
    }

    /// <summary>Returns the version token from <c>hc --version</c> (e.g. <c>0.1.19</c>).</summary>
    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunRawAsync(["--version"], _options.CommandTimeout, cancellationToken).ConfigureAwait(false);
        var parts = result.StdOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length == 2 && parts[0] == "hc"
            ? parts[1]
            : throw new HerdrProtocolException($"Unexpected 'hc --version' output: '{result.StdOut.Trim()}'.");
    }

    public async Task<JsonElement> GetServerStatusAsync(CancellationToken cancellationToken = default) =>
        await RunJsonAsync(["server", "status", "--json"], _options.CommandTimeout, cancellationToken).ConfigureAwait(false);

    /// <summary>Starts the core for the configured session (a no-op status read when already running).</summary>
    public async Task<JsonElement> StartServerAsync(CancellationToken cancellationToken = default) =>
        await RunJsonAsync(["server", "start", "--json"], _options.CommandTimeout, cancellationToken).ConfigureAwait(false);

    public async Task<JsonElement> StopServerAsync(CancellationToken cancellationToken = default) =>
        await RunJsonAsync(["server", "stop", "--json"], _options.CommandTimeout, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<HerdrTerminal>> ListAsync(bool includeFinished = false, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "ls", "--json" };

        if (includeFinished)
            args.Add("--all");

        var root = await RunJsonAsync(args, _options.CommandTimeout, cancellationToken).ConfigureAwait(false);

        return root.GetProperty("terminals").EnumerateArray().Select(HerdrTerminal.FromJson).ToList();
    }

    public async Task<HerdrTerminal> GetAsync(string terminal, CancellationToken cancellationToken = default)
    {
        var root = await RunJsonAsync(["get", "--json", terminal], _options.CommandTimeout, cancellationToken).ConfigureAwait(false);

        return HerdrTerminal.FromJson(root);
    }

    /// <summary>Starts a terminal without waiting for it and without attaching it to any parent terminal.</summary>
    public async Task<HerdrTerminal> RunBackgroundAsync(HerdrRunRequest request, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "run", "--json", "--bg", "--no-parent" };

        if (request.Name is not null)
            args.AddRange(["--name", request.Name]);

        if (request.WorkingDirectory is not null)
            args.AddRange(["--cwd", request.WorkingDirectory]);

        foreach (var (key, value) in request.Labels ?? new Dictionary<string, string>())
            args.AddRange(["--label", $"{key}={value}"]);

        // Everything after "--" is the program's own argv, never parsed as an hc option.
        args.Add("--");
        args.AddRange(request.Argv);

        var root = await RunJsonAsync(args, _options.CommandTimeout, cancellationToken).ConfigureAwait(false);

        // `run --bg` replies with a short record; fetch the full facts so callers get one shape.
        return await GetAsync(root.GetProperty("address").GetString()!, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAsync(string terminal, string text, CancellationToken cancellationToken = default) =>
        await RunJsonAsync(["send", "--json", "--", terminal, text], _options.CommandTimeout, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Waits for the terminal to stop working/exit, or (with <paramref name="output"/>) for text printed
    /// after the wait started. A timeout is a normal check-in: <see cref="HerdrWaitResult.Ended"/> is false.
    /// </summary>
    public async Task<HerdrWaitResult> WaitAsync(
        string terminal,
        TimeSpan timeout,
        string? output = null,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string>
        {
            "wait",
            "--json",
            "--timeout", timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture),
        };

        if (output is not null)
            args.AddRange(["--output", output]);

        args.Add(terminal);

        var root = await RunJsonAsync(args, timeout + WaitProcessMargin, cancellationToken).ConfigureAwait(false);

        return HerdrWaitResult.FromJson(root);
    }

    public async Task<HerdrReadResult> ReadAsync(string terminal, int? lines = null, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "read", "--json" };

        if (lines is not null)
            args.AddRange(["--lines", lines.Value.ToString(CultureInfo.InvariantCulture)]);

        args.Add(terminal);

        var root = await RunJsonAsync(args, _options.CommandTimeout, cancellationToken).ConfigureAwait(false);

        return new HerdrReadResult(
            root.GetProperty("text").GetString() ?? string.Empty,
            root.GetProperty("complete").GetBoolean(),
            root.GetProperty("how").GetString() ?? string.Empty);
    }

    public async Task KillAsync(string terminal, CancellationToken cancellationToken = default) =>
        await RunJsonAsync(["kill", "--json", terminal], _options.CommandTimeout, cancellationToken).ConfigureAwait(false);

    public async Task RemoveAsync(string terminal, CancellationToken cancellationToken = default) =>
        await RunJsonAsync(["rm", "--json", terminal], _options.CommandTimeout, cancellationToken).ConfigureAwait(false);

    private async Task<JsonElement> RunJsonAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = await RunRawAsync(arguments, timeout, cancellationToken).ConfigureAwait(false);

        try
        {
            // Clone so the element outlives the disposed document.
            using var document = JsonDocument.Parse(result.StdOut);

            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new HerdrProtocolException($"'hc {arguments[0]}' returned malformed JSON: '{Truncate(result.StdOut)}'.", exception);
        }
    }

    private async Task<HcProcessResult> RunRawAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(arguments, timeout, cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw ToCommandException(result);

        return result;
    }

    private static HerdrCommandException ToCommandException(HcProcessResult result)
    {
        // hc reports errors as {"error":{"kind","message","hint"}} on stderr.
        try
        {
            using var document = JsonDocument.Parse(result.StdErr);
            var error = document.RootElement.GetProperty("error");

            return new HerdrCommandException(
                result.ExitCode,
                error.GetStringOrNull("kind"),
                error.GetStringOrNull("message") ?? "(no message)",
                error.GetStringOrNull("hint"));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            var text = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;

            return new HerdrCommandException(result.ExitCode, null, Truncate(text), null);
        }
    }

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "...";
}

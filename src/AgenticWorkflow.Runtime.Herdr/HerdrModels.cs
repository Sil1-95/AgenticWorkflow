using System.Text.Json;

namespace AgenticWorkflow.Runtime.Herdr;

/// <summary>The program's own status word (OSC 7501 root record). Null when the program never reported one.</summary>
internal sealed record HerdrStatus(string State, string? App, string? Kind, string? Message)
{
    public static HerdrStatus? FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        return new HerdrStatus(
            element.GetProperty("state").GetString() ?? string.Empty,
            element.GetStringOrNull("app"),
            element.GetStringOrNull("kind"),
            element.GetStringOrNull("msg"));
    }
}

/// <summary>The subset of a Herdr terminal's facts that AgenticWorkflow consumes.</summary>
internal sealed record HerdrTerminal(
    string Address,
    string? Name,
    string State,
    int? ExitCode,
    string? Reason,
    long Turn,
    HerdrStatus? Status,
    IReadOnlyDictionary<string, string> Labels)
{
    public bool IsLive => State == "live";

    public static HerdrTerminal FromJson(JsonElement element)
    {
        var labels = new Dictionary<string, string>();

        if (element.TryGetProperty("labels", out var labelsElement) && labelsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var label in labelsElement.EnumerateObject())
                labels[label.Name] = label.Value.GetString() ?? string.Empty;
        }

        return new HerdrTerminal(
            element.GetProperty("address").GetString()!,
            element.GetStringOrNull("name"),
            element.GetProperty("state").GetString()!,
            element.GetIntOrNull("exit_code"),
            element.GetStringOrNull("reason"),
            element.TryGetProperty("turn", out var turn) ? turn.GetInt64() : 0,
            element.TryGetProperty("status", out var status) ? HerdrStatus.FromJson(status) : null,
            labels);
    }
}

/// <summary>Result of <c>terminal.wait</c>. <see cref="Ended"/> is false for a timeout check-in.</summary>
internal sealed record HerdrWaitResult(bool Ended, string? Reason, int? ExitCode, HerdrStatus? Status, long Turn)
{
    public static HerdrWaitResult FromJson(JsonElement element) => new(
        element.GetProperty("ended").GetBoolean(),
        element.GetStringOrNull("reason"),
        element.GetIntOrNull("exit_code"),
        element.TryGetProperty("status", out var status) ? HerdrStatus.FromJson(status) : null,
        element.TryGetProperty("turn", out var turn) && turn.ValueKind == JsonValueKind.Number ? turn.GetInt64() : 0);
}

/// <summary>Result of <c>terminal.read</c>.</summary>
internal sealed record HerdrReadResult(string Text, bool Complete, string How);

/// <summary>Arguments for starting a background terminal.</summary>
internal sealed record HerdrRunRequest(
    IReadOnlyList<string> Argv,
    string? Name = null,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Labels = null);

internal static class JsonElementExtensions
{
    public static string? GetStringOrNull(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? GetIntOrNull(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;
}

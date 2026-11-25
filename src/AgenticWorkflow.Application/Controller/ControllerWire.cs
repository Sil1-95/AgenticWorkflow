using System.Buffers.Binary;
using System.Text.Json;
using AgenticWorkflow.Application.ReadModel;

namespace AgenticWorkflow.Application.Controller;

/// <summary>A frame that breaks the framing rules (too large, empty, cut short). The stream cannot be resynchronized: the connection ends.</summary>
public sealed class ControllerFrameException(string message) : Exception(message);

/// <summary>
/// The encoding of the controller protocol: one JSON document per frame, a frame being a 4-byte big-endian length followed by that many bytes of UTF-8. It works on any
/// <see cref="Stream"/>, so it is the same over a named pipe, a Unix-domain socket or an in-memory pair in a test.
/// <para>
/// The controller reads strictly (a message with a property it does not know is refused, never ignored) and a client reads leniently (a reply with a property it does not
/// know is accepted, per the additive-compatibility rule of <see cref="TaskReadModel"/>). The declared length is checked before anything is allocated, so a hostile or
/// broken peer cannot make either side buffer more than the protocol's limits.
/// </para>
/// </summary>
public static class ControllerWire
{
    private static readonly JsonSerializerOptions Lenient = TaskReadModelJson.CreateOptions(indented: false);

    private static readonly JsonSerializerOptions Strict = Configure(TaskReadModelJson.CreateOptions(indented: false));

    private static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;

        return options;
    }

    public static byte[] Encode<T>(T message) => JsonSerializer.SerializeToUtf8Bytes(message, Lenient);

    // ---- frames -----------------------------------------------------------------------------------------------------

    /// <summary>The next frame's payload, or null when the peer closed the stream cleanly between frames. Throws <see cref="ControllerFrameException"/> for a broken frame.</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var read = await ReadAsync(stream, header, cancellationToken).ConfigureAwait(false);

        if (read == 0)
            return null;

        if (read < header.Length)
            throw new ControllerFrameException("The frame header was cut short.");

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);

        if (length == 0)
            throw new ControllerFrameException("An empty frame.");

        if (length > (uint)maxBytes)
            throw new ControllerFrameException($"A frame of {length} bytes exceeds the limit of {maxBytes}.");

        var payload = new byte[length];

        if (await ReadAsync(stream, payload, cancellationToken).ConfigureAwait(false) < payload.Length)
            throw new ControllerFrameException("The frame was cut short.");

        return payload;
    }

    public static async Task WriteFrameAsync(Stream stream, byte[] payload, int maxBytes, CancellationToken cancellationToken)
    {
        if (payload.Length == 0 || payload.Length > maxBytes)
            throw new ControllerFrameException($"A frame of {payload.Length} bytes is outside 1..{maxBytes}.");

        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);

        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);

            if (read == 0)
                break;

            total += read;
        }

        return total;
    }

    // ---- decoding -----------------------------------------------------------------------------------------------------

    /// <summary>Decodes a request the controller received. A message that is not valid is returned as the error to reply with; nothing is thrown for bad input.</summary>
    public static (ControllerRequest? Request, ControllerError? Error) DecodeRequest(byte[] payload)
    {
        string? operation;

        try
        {
            using var document = JsonDocument.Parse(payload);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("operation", out var name)
                || name.ValueKind != JsonValueKind.String)
            {
                return (null, new ControllerError(ControllerErrorCodes.MalformedRequest, "A request is a JSON object with an 'operation'."));
            }

            operation = name.GetString();
        }
        catch (JsonException)
        {
            return (null, new ControllerError(ControllerErrorCodes.MalformedRequest, "The request is not valid JSON."));
        }

        if (operation is null || !ControllerOperations.All.Contains(operation))
            return (null, new ControllerError(ControllerErrorCodes.UnsupportedOperation, "This controller has no such operation."));

        try
        {
            return (JsonSerializer.Deserialize<ControllerRequest>(payload, Strict), null);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            return (null, new ControllerError(ControllerErrorCodes.MalformedRequest, $"The '{operation}' request is not well-formed."));
        }
    }

    /// <summary>Decodes the hello the controller received; an invalid one is returned as the error to refuse it with.</summary>
    public static (ControllerHello? Hello, ControllerError? Error) DecodeHello(byte[] payload)
    {
        try
        {
            var hello = JsonSerializer.Deserialize<ControllerHello>(payload, Strict);

            return hello is null || string.IsNullOrWhiteSpace(hello.Client) || hello.Client.Length > 64
                ? (null, new ControllerError(ControllerErrorCodes.MalformedRequest, "The hello needs a short client name."))
                : (hello, null);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return (null, new ControllerError(ControllerErrorCodes.MalformedRequest, "The first message must be a hello."));
        }
    }

    /// <summary>Decodes a reply or a welcome a client received. Throws <see cref="ControllerFrameException"/> when it is not one.</summary>
    public static T DecodeFromController<T>(byte[] payload)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, Lenient) ?? throw new ControllerFrameException("The controller sent an empty message.");
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            throw new ControllerFrameException("The controller sent a message that is not part of the protocol.");
        }
    }
}

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApnaRemote.Core;

/// <summary>
/// Bounded control-message codec for a future authenticated signaling stream.
/// Encryption/authentication, read deadlines, rate limits and connection cancellation are caller responsibilities.
/// Never use this frame format as a bulk-video transport.
/// </summary>
public static class ProtocolFraming
{
    public const int MaximumControlBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public static byte[] Encode(InputCommand command)
    {
        if (!InputValidation.IsValid(command)) throw new InvalidDataException("Invalid input command.");
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(command, Options);
        if (data.Length > MaximumControlBytes) throw new InvalidDataException("Control payload too large.");
        byte[] frame = new byte[4 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), data.Length);
        data.CopyTo(frame, 4);
        return frame;
    }

    public static async ValueTask<InputCommand> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 2 or > MaximumControlBytes) throw new InvalidDataException("Invalid control frame length.");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        try
        {
            using JsonDocument json = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a control object.");
            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in json.RootElement.EnumerateObject())
                if (!properties.Add(property.Name)) throw new InvalidDataException("Duplicate control property.");
            InputCommand? command = JsonSerializer.Deserialize<InputCommand>(payload, Options);
            if (command is null || !InputValidation.IsValid(command)) throw new InvalidDataException("Invalid control payload.");
            return command;
        }
        catch (JsonException ex) { throw new InvalidDataException("Malformed control payload.", ex); }
    }
}

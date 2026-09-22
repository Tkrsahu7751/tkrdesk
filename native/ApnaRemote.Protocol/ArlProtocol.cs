using System.Buffers.Binary;
using System.Text;
using ApnaRemote.Core;

namespace ApnaRemote.Protocol;

/// <summary>
/// Length-prefixed ARL1 framing for attended LAN share (TLS outer). JPEG and optional H.264 frames.
/// </summary>
public static class ArlProtocol
{
    public const int DefaultPort = 5720;
    public const uint Magic = 0x314C5241; // 'ARL1' little-endian
    public const int MaxPayload = 8 * 1024 * 1024;
    public const int Revision = 4; // TLS + cert-bound PIN + optional H.264 frames (FrameH264).

    /// <summary>0 = JPEG frames, 1 = H.264 Annex-B access units.</summary>
    public const int StreamCodecJpeg = 0;
    public const int StreamCodecH264 = 1;

    /// <summary>Per-type caps checked before allocating the payload buffer.</summary>
    public static int MaxPayloadFor(MessageType type) => type switch
    {
        MessageType.HelloViewer => 8 * 1024,
        MessageType.HelloOk => 32,
        MessageType.HelloReject => 4 * 1024,
        MessageType.WaitingApproval => 0,
        MessageType.Approved => 32,
        MessageType.Rejected => 4 * 1024,
        MessageType.FrameJpeg => MaxPayload,
        MessageType.Heartbeat => 0,
        MessageType.End => 4 * 1024,
        MessageType.InputControl => ProtocolFraming.MaximumControlBytes + 4,
        MessageType.PermissionChanged => 12,
        MessageType.PermissionAck => 12,
        MessageType.FrameH264 => MaxPayload,
        MessageType.ClipboardText => 1024 * 1024,
        MessageType.DirectNotice => 64 * 1024,
        MessageType.FileChunk => 4 * 1024 * 1024,
        _ => 1024,
    };

    public enum MessageType : uint
    {
        HelloViewer = 1,
        HelloOk = 2,
        HelloReject = 3,
        WaitingApproval = 4,
        Approved = 5,
        Rejected = 6,
        FrameJpeg = 7,
        Heartbeat = 8,
        End = 9,
        InputControl = 10,
        PermissionChanged = 11,
        PermissionAck = 12,
        FrameH264 = 13,
        ClipboardText = 14,
        DirectNotice = 15,
        FileChunk = 16,
    }

    public static readonly TimeSpan WriteDeadline = TimeSpan.FromSeconds(5);

    public static async Task WriteMessageAsync(Stream stream, MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > MaxPayload)
            throw new InvalidOperationException("Payload too large.");

        byte[] header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), (uint)type);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), (uint)payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (!payload.IsEmpty)
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    public static async Task WriteMessageWithDeadlineAsync(
        Stream stream, MessageType type, ReadOnlyMemory<byte> payload, TimeSpan writeDeadline, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(writeDeadline);
        await WriteMessageAsync(stream, type, payload, deadline.Token).ConfigureAwait(false);
    }

    public static async Task<(MessageType Type, byte[] Payload)> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = await ReadExactAsync(stream, 12, ct).ConfigureAwait(false);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        if (magic != Magic)
            throw new InvalidOperationException("Bad protocol magic.");

        var type = (MessageType)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        int typeCap = MaxPayloadFor(type);
        if (length > MaxPayload || length > (uint)typeCap)
            throw new InvalidOperationException("Payload length rejected for message type " + type + ".");

        byte[] payload = length == 0
            ? []
            : await ReadExactAsync(stream, (int)length, ct).ConfigureAwait(false);
        return (type, payload);
    }

    public static byte[] EncodeHelloViewer(string peerId, string displayName, ReadOnlySpan<byte> pinProof)
    {
        if (pinProof.Length != LanTls.PinProofBytes)
            throw new InvalidOperationException("PIN proof must be 32 bytes.");
        using var ms = new MemoryStream();
        WriteString(ms, peerId);
        WriteString(ms, displayName);
        ms.Write(pinProof);
        ms.Write(BitConverter.GetBytes(Revision));
        return ms.ToArray();
    }

    public static (string PeerId, string DisplayName, byte[] PinProof) DecodeHelloViewer(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        string peerId = reader.ReadString();
        string displayName = reader.ReadString();
        byte[] pinProof = reader.ReadExact(LanTls.PinProofBytes);
        reader.RequireRevision();
        return (peerId, displayName, pinProof);
    }

    public static byte[] EncodeGuid(Guid id)
    {
        byte[] bytes = new byte[20];
        id.TryWriteBytes(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), Revision);
        return bytes;
    }

    public static Guid DecodeGuid(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 20 || BinaryPrimitives.ReadInt32LittleEndian(payload[16..]) != Revision)
            throw new InvalidOperationException("Incompatible lab version. Install matching Apna Remote on both devices.");

        return new Guid(payload[..16]);
    }

    public static byte[] EncodePermission(int epoch, bool control, bool paused = false)
    {
        byte[] payload = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(payload, epoch);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), control ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), paused ? 1 : 0);
        return payload;
    }

    public static (int Epoch, bool Control, bool Paused) DecodePermission(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 12) throw new InvalidOperationException("Invalid permission message.");
        int epoch = BinaryPrimitives.ReadInt32LittleEndian(payload);
        int control = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        int paused = BinaryPrimitives.ReadInt32LittleEndian(payload[8..]);
        if (epoch < 1 || control is < 0 or > 1 || paused is < 0 or > 1 || (control == 1 && paused == 1))
            throw new InvalidOperationException("Invalid permission state.");
        return (epoch, control == 1, paused == 1);
    }

    public static byte[] EncodeApproved(int width, int height, bool allowControl, int permissionEpoch, int streamCodec = StreamCodecJpeg)
    {
        if (streamCodec is not (StreamCodecJpeg or StreamCodecH264))
            throw new ArgumentOutOfRangeException(nameof(streamCodec));

        byte[] bytes = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), height);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), allowControl ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12, 4), permissionEpoch);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16, 4), streamCodec);
        return bytes;
    }

    public static (int Width, int Height, bool AllowControl, int PermissionEpoch, int StreamCodec) DecodeApproved(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
            throw new InvalidOperationException("Approved payload invalid.");

        int width = BinaryPrimitives.ReadInt32LittleEndian(payload[..4]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));
        bool allow = payload.Length >= 12 && BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(8, 4)) != 0;
        int epoch = payload.Length >= 16 ? BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(12, 4)) : 1;
        int codec = payload.Length >= 20 ? BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(16, 4)) : StreamCodecJpeg;
        if (codec is not (StreamCodecJpeg or StreamCodecH264))
            throw new InvalidOperationException("Unsupported stream codec.");
        return (width, height, allow, Math.Max(1, epoch), codec);
    }

    public static byte[] EncodeString(string value)
    {
        using var ms = new MemoryStream();
        WriteString(ms, value);
        return ms.ToArray();
    }

    public static string DecodeString(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        return reader.ReadString();
    }

    private static void WriteString(Stream stream, string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value ?? "");
        if (utf8.Length > 1024)
            throw new InvalidOperationException("String too long.");

        Span<byte> len = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(len, (ushort)utf8.Length);
        stream.Write(len);
        stream.Write(utf8);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Connection closed.");

            offset += read;
        }

        return buffer;
    }

    private ref struct SpanReader
    {
        private ReadOnlySpan<byte> _span;

        public SpanReader(ReadOnlySpan<byte> span) => _span = span;

        public void RequireRevision()
        {
            if (_span.Length != 4 || BinaryPrimitives.ReadInt32LittleEndian(_span) != Revision)
                throw new InvalidOperationException("Incompatible lab version. Install matching Apna Remote on both devices.");
        }

        public byte[] ReadExact(int count)
        {
            if (_span.Length < count)
                throw new InvalidOperationException("Truncated binary field.");
            byte[] value = _span[..count].ToArray();
            _span = _span[count..];
            return value;
        }

        public string ReadString()
        {
            if (_span.Length < 2)
                throw new InvalidOperationException("Truncated string.");

            ushort len = BinaryPrimitives.ReadUInt16LittleEndian(_span);
            _span = _span[2..];
            if (_span.Length < len)
                throw new InvalidOperationException("Truncated string bytes.");

            string value = Encoding.UTF8.GetString(_span[..len]);
            _span = _span[len..];
            return value;
        }
    }
}

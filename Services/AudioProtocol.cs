using System.Buffers.Binary;

namespace ClientAvalonia.Services;

public static class AudioProtocol
{
    public const byte Version = 2;
    public const byte FlagDiscontinuity = 0x01;
    public const int InputHeaderSize = 32;
    public const int OutputHeaderSize = 40;

    public readonly record struct OutputHeader(
        byte Flags,
        ulong SessionId,
        uint Sequence,
        uint SampleRate,
        ulong TimestampNs,
        ushort ProcessingMs,
        ushort InputQueueMs,
        ushort OutputQueueMs);

    public static byte[] BuildInputFrame(
        ulong sessionId,
        uint sequence,
        ulong timestampNs,
        byte flags,
        byte[] payload,
        int payloadLength)
    {
        if (payloadLength <= 0 || payloadLength > payload.Length || payloadLength % 4 != 0)
            throw new ArgumentOutOfRangeException(nameof(payloadLength));

        var frame = new byte[InputHeaderSize + payloadLength];
        frame[0] = (byte)'R'; frame[1] = (byte)'V'; frame[2] = (byte)'C'; frame[3] = (byte)'A';
        frame[4] = Version;
        frame[5] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6, 2), InputHeaderSize);
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(8, 8), sessionId);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(16, 4), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(20, 4), 16000);
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(24, 8), timestampNs);
        Buffer.BlockCopy(payload, 0, frame, InputHeaderSize, payloadLength);
        return frame;
    }

    public static bool TryParseOutputFrame(byte[] frame, out OutputHeader header, out int payloadOffset)
    {
        header = default;
        payloadOffset = 0;
        if (frame.Length < OutputHeaderSize
            || frame[0] != 'R' || frame[1] != 'V' || frame[2] != 'C' || frame[3] != 'O'
            || frame[4] != Version)
            return false;

        int headerLength = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(6, 2));
        if (headerLength != OutputHeaderSize || frame.Length <= headerLength)
            return false;

        header = new OutputHeader(
            frame[5],
            BinaryPrimitives.ReadUInt64BigEndian(frame.AsSpan(8, 8)),
            BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(16, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(20, 4)),
            BinaryPrimitives.ReadUInt64BigEndian(frame.AsSpan(24, 8)),
            BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(32, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(34, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(36, 2)));
        payloadOffset = headerLength;
        return header.SampleRate == 16000 && (frame.Length - payloadOffset) % 4 == 0;
    }
}

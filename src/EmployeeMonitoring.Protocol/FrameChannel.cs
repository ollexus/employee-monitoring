using System.Buffers.Binary;

namespace EmployeeMonitoring.Protocol;

/// <summary>
/// Тип кадра протокола. Каждый кадр: [1 байт тип][4 байта длина (int32, big-endian)][payload].
/// </summary>
public enum FrameType : byte
{
    None = 0,
    Auth = 1,
    AuthResult = 2,
    Heartbeat = 3,
    Command = 4,
    ScreenshotMeta = 5,
    ScreenshotData = 6,
    CommandAck = 7,
    Log = 8
}

public static class ProtocolConstants
{
    public const int DefaultAgentPort = 45800;
    public const string AgentHandshakeTokenHeader = "X-Agent-Token";
    public const int MaxPayloadBytes = 16 * 1024 * 1024;
    public const int MaxCommandsPerSecond = 30;
}

public readonly record struct Frame(FrameType Type, byte[] Payload)
{
    public bool HasPayload => Payload.Length > 0;
}

/// <summary>
/// Утилизация сетевого формата кадров поверх произвольного потока (TCP).
/// Никаких сторонних библиотек: только System.Buffers.Binary и System.Text.Json.
/// </summary>
public static class FrameChannel
{
    private const int HeaderSize = 5;

    public static async Task WriteAsync<T>(Stream stream, FrameType type, T payload, CancellationToken cancellationToken)
    {
        byte[] body = payload is null ? Array.Empty<byte>() : Json.Serialize(payload);
        await WriteRawAsync(stream, type, body, cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteRawAsync(Stream stream, FrameType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        byte[] header = new byte[HeaderSize];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length);

        // Заголовок и тело отправляются одной операцией, чтобы исключить перемешивание
        // кадров при параллельной отправке (вызывающая сторона дополнительно сериализует запись).
        byte[] packet = new byte[HeaderSize + payload.Length];
        header.CopyTo(packet, 0);
        payload.Span.CopyTo(packet.AsSpan(HeaderSize));

        await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<Frame?> ReadAsync(Stream stream, int maxPayloadBytes, CancellationToken cancellationToken)
    {
        byte[] header = await ReadExactlyAsync(stream, HeaderSize, cancellationToken).ConfigureAwait(false);
        if (header.Length < HeaderSize)
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (length < 0 || length > maxPayloadBytes)
        {
            throw new InvalidDataException($"Некорректная длина кадра: {length}");
        }

        byte[] payload = length == 0
            ? Array.Empty<byte>()
            : await ReadExactlyAsync(stream, length, cancellationToken).ConfigureAwait(false);

        if (payload.Length < length)
        {
            return null;
        }

        return new Frame((FrameType)header[0], payload);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                return buffer.AsSpan(0, offset).ToArray();
            }

            offset += read;
        }

        return buffer;
    }
}

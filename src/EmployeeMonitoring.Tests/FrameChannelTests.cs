using System.Text;
using EmployeeMonitoring.Protocol;

namespace EmployeeMonitoring.Tests;

/// <summary>Тесты формата кадра: разбиение и склейка TCP-потока, границы размера, конец потока.</summary>
public static class FrameChannelTests
{
    [Test]
    public static async Task RoundTripPreservesTypeAndPayload()
    {
        using var stream = new MemoryStream();
        byte[] payload = Encoding.UTF8.GetBytes("{\"clientId\":\"abc\",\"machineName\":\"PC-1\"}");

        await FrameChannel.WriteRawAsync(stream, FrameType.Heartbeat, payload, CancellationToken.None);

        stream.Position = 0;
        Frame? frame = await FrameChannel.ReadAsync(stream, ProtocolConstants.MaxPayloadBytes, CancellationToken.None);

        Assert.NotNull(frame, "кадр должен быть прочитан");
        Assert.Equal(FrameType.Heartbeat, frame!.Value.Type, "тип кадра искажён");
        Assert.BytesEqual(payload, frame.Value.Payload);
    }

    [Test]
    public static async Task RoundTripForEveryFrameType()
    {
        foreach (FrameType type in Enum.GetValues<FrameType>())
        {
            using var stream = new MemoryStream();
            byte[] payload = [(byte)type, 0, 1, 2, 255];

            await FrameChannel.WriteRawAsync(stream, type, payload, CancellationToken.None);
            stream.Position = 0;
            Frame? frame = await FrameChannel.ReadAsync(stream, ProtocolConstants.MaxPayloadBytes, CancellationToken.None);

            Assert.NotNull(frame, $"кадр {type} не прочитан");
            Assert.Equal(type, frame!.Value.Type);
            Assert.BytesEqual(payload, frame.Value.Payload);
        }
    }

    [Test]
    public static async Task EmptyPayloadIsSupported()
    {
        using var stream = new MemoryStream();
        await FrameChannel.WriteRawAsync(stream, FrameType.Log, ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        stream.Position = 0;
        Frame? frame = await FrameChannel.ReadAsync(stream, ProtocolConstants.MaxPayloadBytes, CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(0, frame!.Value.Payload.Length, "пустой payload должен оставаться пустым");
    }

    [Test]
    public static async Task SeveralFramesAreReadInOrder()
    {
        using var stream = new MemoryStream();
        for (int i = 0; i < 5; i++)
        {
            await FrameChannel.WriteAsync(stream, FrameType.Log, new AgentLogEntry { Message = $"запись {i}" }, CancellationToken.None);
        }

        stream.Position = 0;
        for (int i = 0; i < 5; i++)
        {
            Frame? frame = await FrameChannel.ReadAsync(stream, ProtocolConstants.MaxPayloadBytes, CancellationToken.None);
            Assert.NotNull(frame, $"кадр {i} не прочитан");

            var entry = Json.Deserialize<AgentLogEntry>(frame!.Value.Payload);
            Assert.NotNull(entry);
            Assert.Equal($"запись {i}", entry!.Message, "порядок кадров нарушен");
        }

        Assert.Null(await FrameChannel.ReadAsync(stream, ProtocolConstants.MaxPayloadBytes, CancellationToken.None), "после всех кадров ожидался конец потока");
    }

    /// <summary>TCP отдаёт данные порциями: кадр должен собираться из нескольких чтений.</summary>
    [Test]
    public static async Task FrameIsAssembledFromFragmentedStream()
    {
        using var source = new MemoryStream();
        byte[] payload = new byte[5000];
        Random.Shared.NextBytes(payload);
        await FrameChannel.WriteRawAsync(source, FrameType.ScreenshotData, payload, CancellationToken.None);

        using var stream = new FragmentedStream(source.ToArray(), chunkSize: 7);
        Frame? frame = await FrameChannel.ReadAsync(stream, ProtocolConstants.MaxPayloadBytes, CancellationToken.None);

        Assert.NotNull(frame, "кадр из фрагментированного потока не собран");
        Assert.Equal(FrameType.ScreenshotData, frame!.Value.Type);
        Assert.BytesEqual(payload, frame.Value.Payload);
    }

    [Test]
    public static async Task SeveralFramesFromFragmentedStream()
    {
        using var source = new MemoryStream();
        for (int i = 0; i < 4; i++)
        {
            await FrameChannel.WriteAsync(source, FrameType.Heartbeat, new Heartbeat { IdleSeconds = i }, CancellationToken.None);
        }

        using var stream = new FragmentedStream(source.ToArray(), chunkSize: 3);
        for (int i = 0; i < 4; i++)
        {
            Frame? frame = await FrameChannel.ReadAsync(stream, ProtocolConstants.MaxPayloadBytes, CancellationToken.None);
            Assert.NotNull(frame, $"кадр {i} не прочитан");
            Assert.Equal(i, Json.Deserialize<Heartbeat>(frame!.Value.Payload)!.IdleSeconds);
        }
    }

    [Test]
    public static async Task EndOfStreamReturnsNull()
    {
        using var stream = new MemoryStream();
        Assert.Null(await FrameChannel.ReadAsync(stream, ProtocolConstants.MaxPayloadBytes, CancellationToken.None));
    }

    [Test]
    public static async Task TruncatedHeaderReturnsNull()
    {
        using var stream = new MemoryStream([(byte)FrameType.Heartbeat, 0, 0]);
        Assert.Null(await FrameChannel.ReadAsync(stream, ProtocolConstants.MaxPayloadBytes, CancellationToken.None));
    }

    [Test]
    public static async Task OversizedLengthIsRejected()
    {
        using var stream = new MemoryStream([(byte)FrameType.ScreenshotData, 0xFF, 0xFF, 0xFF, 0x00]);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => FrameChannel.ReadAsync(stream, maxPayloadBytes: 1024, CancellationToken.None),
            "кадр с недопустимой длиной должен отклоняться");
    }

    [Test]
    public static async Task NegativeLengthIsRejected()
    {
        using var stream = new MemoryStream([(byte)FrameType.ScreenshotData, 0xFF, 0xFF, 0xFF, 0xFF]);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => FrameChannel.ReadAsync(stream, maxPayloadBytes: 1024, CancellationToken.None));
    }

    [Test]
    public static async Task LargeScreenshotFrameSurvivesRoundTrip()
    {
        using var stream = new MemoryStream();
        byte[] jpeg = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(jpeg);

        await FrameChannel.WriteRawAsync(stream, FrameType.ScreenshotData, jpeg, CancellationToken.None);
        stream.Position = 0;
        Frame? frame = await FrameChannel.ReadAsync(stream, ProtocolConstants.MaxPayloadBytes, CancellationToken.None);

        Assert.NotNull(frame);
        Assert.BytesEqual(jpeg, frame!.Value.Payload);
    }

    [Test]
    public static async Task CancellationStopsWaiting()
    {
        using var stream = new NeverEndingStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => FrameChannel.ReadAsync(stream, ProtocolConstants.MaxPayloadBytes, cts.Token));
    }

    private sealed class FragmentedStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int length = Math.Min(Math.Min(chunkSize, count), data.Length - _position);
            if (length <= 0)
            {
                return 0;
            }

            Array.Copy(data, _position, buffer, offset, length);
            _position += length;
            return length;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(Math.Min(chunkSize, buffer.Length), data.Length - _position);
            if (length <= 0)
            {
                return ValueTask.FromResult(0);
            }

            data.AsSpan(_position, length).CopyTo(buffer.Span);
            _position += length;
            return ValueTask.FromResult(length);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>Поток никогда не возвращает данные, но уважает отмену.</summary>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

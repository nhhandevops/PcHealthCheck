using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcHealth.Ipc;

/// <summary>
/// Length-prefixed JSON framing over a pipe stream (4-byte little-endian length,
/// then UTF-8 payload). The length cap keeps a misbehaving peer from forcing a
/// huge allocation.
/// </summary>
public sealed class PipeChannel(Stream stream)
{
    private const int MaxMessageBytes = 32 * 1024 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task SendAsync(PipeMessage message, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length > MaxMessageBytes)
            throw new InvalidOperationException($"Message too large to send ({payload.Length} bytes).");

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads the next message, or null when the peer closed the pipe.</summary>
    public async Task<PipeMessage?> ReceiveAsync(CancellationToken ct = default)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(header, ct).ConfigureAwait(false)) return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageBytes)
            throw new InvalidOperationException($"Invalid message length {length} on the control pipe.");

        var payload = new byte[length];
        if (!await ReadExactlyAsync(payload, ct).ConfigureAwait(false))
            throw new EndOfStreamException("Pipe closed mid-message.");

        return JsonSerializer.Deserialize<PipeMessage>(payload, JsonOptions);
    }

    private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0) return offset != 0 ? throw new EndOfStreamException("Pipe closed mid-message.") : false;
            offset += read;
        }
        return true;
    }
}

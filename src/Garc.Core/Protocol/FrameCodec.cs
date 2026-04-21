using System.Buffers.Binary;

namespace Garc.Core.Protocol;

/// <summary>
/// Codec del framing LSP/1. Máquina de bytes pura; no asume TCP.
/// Formato big-endian: <c>[len:4B][type:1B][payload:len-1B]</c>, <c>len = 1 + payload.Length</c>.
/// </summary>
public static class FrameCodec
{
    /// <summary>Límite máximo: 16 MiB (spec §2).</summary>
    public const int MaxFrameSize = 16 * 1024 * 1024;

    /// <summary>Serializa un frame al formato de wire.</summary>
    public static byte[] Encode(Frame frame)
    {
        var declaredLen = frame.Payload.Length + 1; // + type byte
        if (declaredLen > MaxFrameSize)
        {
            throw LandspeedException.FrameTooLarge((uint)Math.Min(declaredLen, uint.MaxValue), MaxFrameSize);
        }
        var output = new byte[4 + declaredLen];
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), (uint)declaredLen);
        output[4] = (byte)frame.Type;
        frame.Payload.Span.CopyTo(output.AsSpan(5));
        return output;
    }
}

/// <summary>
/// Parser streaming: acumula bytes y emite frames completos a medida que llegan.
/// Tras un error el parser queda en estado inválido; no se debe seguir usando.
/// </summary>
public sealed class FrameStreamParser
{
    private readonly int _maxFrameSize;
    private byte[] _buffer;
    private int _length;

    public FrameStreamParser(int maxFrameSize = FrameCodec.MaxFrameSize)
    {
        _maxFrameSize = maxFrameSize;
        _buffer = new byte[4096];
        _length = 0;
    }

    public int PendingBytes => _length;

    public List<Frame> Feed(ReadOnlySpan<byte> data)
    {
        if (data.Length > 0)
        {
            EnsureCapacity(_length + data.Length);
            data.CopyTo(_buffer.AsSpan(_length));
            _length += data.Length;
        }
        var frames = new List<Frame>();
        while (TryExtract(out var frame))
        {
            frames.Add(frame);
        }
        return frames;
    }

    private bool TryExtract(out Frame frame)
    {
        frame = null!;
        if (_length < 4) return false;

        var declaredLen = BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(0, 4));

        if (declaredLen == 0)
        {
            throw LandspeedException.FrameLengthZero();
        }
        if (declaredLen > (uint)_maxFrameSize)
        {
            throw LandspeedException.FrameTooLarge(declaredLen, _maxFrameSize);
        }

        var total = 4 + (int)declaredLen;
        if (_length < total) return false;

        var typeByte = _buffer[4];
        if (!FrameTypeExtensions.IsKnown(typeByte))
        {
            throw LandspeedException.UnknownFrameType(typeByte);
        }

        var payloadLen = (int)declaredLen - 1;
        var payload = new byte[payloadLen];
        Buffer.BlockCopy(_buffer, 5, payload, 0, payloadLen);

        // Desplazar el buffer.
        var remaining = _length - total;
        if (remaining > 0)
        {
            Buffer.BlockCopy(_buffer, total, _buffer, 0, remaining);
        }
        _length = remaining;

        frame = new Frame((FrameType)typeByte, payload);
        return true;
    }

    private void EnsureCapacity(int required)
    {
        if (_buffer.Length >= required) return;
        var newSize = _buffer.Length;
        while (newSize < required) newSize *= 2;
        var newBuf = new byte[newSize];
        Buffer.BlockCopy(_buffer, 0, newBuf, 0, _length);
        _buffer = newBuf;
    }
}

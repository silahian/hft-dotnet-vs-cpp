using System.Buffers.Binary;

namespace Bench.Core;

/// <summary>
/// Deterministic xorshift64* PRNG. Reimplemented byte-identically in the C++ generator
/// verification path so both languages can be shown to consume the same stream.
/// </summary>
public struct Xorshift64
{
    private ulong _state;

    public Xorshift64(ulong seed) => _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    public ulong Next()
    {
        ulong x = _state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _state = x;
        unchecked { return x * 2685821657736338717UL; }
    }

    /// <summary>Uniform in [0, bound). bound must be > 0.</summary>
    public uint NextBounded(uint bound) => (uint)(Next() % bound);
}

/// <summary>
/// A single L2 ladder event. 16 bytes, identical layout to the C++ <c>BookEvent</c>.
/// </summary>
public readonly struct BookEvent
{
    public readonly int TickIndex;
    public readonly int Side;     // 0 = bid, 1 = ask
    public readonly long Qty;     // 0 = delete this level

    public BookEvent(int tickIndex, int side, long qty)
    {
        TickIndex = tickIndex;
        Side = side;
        Qty = qty;
    }
}

public sealed class BookEventFile
{
    public const uint Magic = 0x5645_4B42; // 'B','K','E','V' little-endian
    public const int HeaderBytes = 32;
    public const int RecordBytes = 16;

    public required int TickCount { get; init; }
    public required BookEvent[] Events { get; init; }

    public static BookEventFile Load(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        if (raw.Length < HeaderBytes) throw new InvalidDataException($"{path}: truncated header");

        var span = raw.AsSpan();
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(span);
        if (magic != Magic) throw new InvalidDataException($"{path}: bad magic 0x{magic:X8}");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        if (version != 1) throw new InvalidDataException($"{path}: unsupported version {version}");
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        int tickCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);

        long expected = (long)HeaderBytes + (long)count * RecordBytes;
        if (raw.LongLength != expected)
            throw new InvalidDataException($"{path}: expected {expected} bytes, got {raw.LongLength}");

        var events = new BookEvent[count];
        var body = span[HeaderBytes..];
        for (int i = 0; i < count; i++)
        {
            var rec = body.Slice(i * RecordBytes, RecordBytes);
            events[i] = new BookEvent(
                BinaryPrimitives.ReadInt32LittleEndian(rec),
                BinaryPrimitives.ReadInt32LittleEndian(rec[4..]),
                BinaryPrimitives.ReadInt64LittleEndian(rec[8..]));
        }

        return new BookEventFile { TickCount = tickCount, Events = events };
    }

    public static void Save(string path, int tickCount, BookEvent[] events)
    {
        var buf = new byte[HeaderBytes + (long)events.Length * RecordBytes];
        var span = buf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)events.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)tickCount);

        var body = span[HeaderBytes..];
        for (int i = 0; i < events.Length; i++)
        {
            var rec = body.Slice(i * RecordBytes, RecordBytes);
            BinaryPrimitives.WriteInt32LittleEndian(rec, events[i].TickIndex);
            BinaryPrimitives.WriteInt32LittleEndian(rec[4..], events[i].Side);
            BinaryPrimitives.WriteInt64LittleEndian(rec[8..], events[i].Qty);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, buf);
    }
}

/// <summary>
/// A blob of FIX 4.4 NewOrderSingle messages plus an offset table.
/// Identical layout to the C++ <c>FixMessageFile</c>.
/// </summary>
public sealed class FixMessageFile
{
    public const uint Magic = 0x4D58_4946; // 'F','I','X','M' little-endian
    public const int HeaderBytes = 32;

    public required int Count { get; init; }
    public required uint[] Offsets { get; init; } // length Count + 1
    public required byte[] Payload { get; init; }

    public static FixMessageFile Load(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        if (raw.Length < HeaderBytes) throw new InvalidDataException($"{path}: truncated header");

        var span = raw.AsSpan();
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(span);
        if (magic != Magic) throw new InvalidDataException($"{path}: bad magic 0x{magic:X8}");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        if (version != 1) throw new InvalidDataException($"{path}: unsupported version {version}");
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        int payloadBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);

        int offsetsBytes = (count + 1) * 4;
        long expected = (long)HeaderBytes + offsetsBytes + payloadBytes;
        if (raw.LongLength != expected)
            throw new InvalidDataException($"{path}: expected {expected} bytes, got {raw.LongLength}");

        var offsets = new uint[count + 1];
        var offSpan = span.Slice(HeaderBytes, offsetsBytes);
        for (int i = 0; i <= count; i++)
            offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(offSpan[(i * 4)..]);

        var payload = span.Slice(HeaderBytes + offsetsBytes, payloadBytes).ToArray();
        return new FixMessageFile { Count = count, Offsets = offsets, Payload = payload };
    }

    public static void Save(string path, uint[] offsets, byte[] payload)
    {
        int count = offsets.Length - 1;
        int offsetsBytes = offsets.Length * 4;
        var buf = new byte[HeaderBytes + offsetsBytes + payload.Length];
        var span = buf.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)payload.Length);

        var offSpan = span.Slice(HeaderBytes, offsetsBytes);
        for (int i = 0; i < offsets.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(offSpan[(i * 4)..], offsets[i]);

        payload.CopyTo(span.Slice(HeaderBytes + offsetsBytes));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, buf);
    }
}

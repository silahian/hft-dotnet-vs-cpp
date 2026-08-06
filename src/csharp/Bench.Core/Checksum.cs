using System.Runtime.CompilerServices;

namespace Bench.Core;

/// <summary>
/// FNV-1a style 64-bit fold. Wrapping arithmetic only, so the C++ <c>uint64_t</c>
/// implementation produces bit-identical output. This is the cross-language
/// correctness oracle and the dead-code-elimination barrier.
/// </summary>
public static class Checksum
{
    public const ulong Offset = 14695981039346656037UL;
    public const ulong Prime = 1099511628211UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Mix(ulong h, ulong v)
    {
        unchecked { return (h ^ v) * Prime; }
    }
}
